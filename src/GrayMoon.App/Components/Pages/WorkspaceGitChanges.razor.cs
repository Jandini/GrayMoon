using GrayMoon.App.Data;
using GrayMoon.App.Models;
using GrayMoon.App.Services;
using GrayMoon.App.Services.GitChanges;
using GrayMoon.Common.Git;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;

namespace GrayMoon.App.Components.Pages;

public sealed partial class WorkspaceGitChanges : IAsyncDisposable
{
    [Parameter] public int WorkspaceId { get; set; }

    [Inject] private IWorkspaceGitChangesReadService ReadService { get; set; } = default!;
    [Inject] private IGitChangesAgentClient AgentClient { get; set; } = default!;
    [Inject] private WorkspaceGitChangesWriteQueue WriteQueue { get; set; } = default!;
    [Inject] private AppDbContext DbContext { get; set; } = default!;
    [Inject] private WorkspaceService WorkspaceService { get; set; } = default!;
    [Inject] private IAgentBridge AgentBridge { get; set; } = default!;
    [Inject] private IToastService ToastService { get; set; } = default!;
    [Inject] private NavigationManager NavigationManager { get; set; } = default!;
    [Inject] private ILogger<WorkspaceGitChanges> Logger { get; set; } = default!;

    private Workspace? _workspace;
    private WorkspaceGitChangesView? _view;
    private IReadOnlyList<GitChangesTreeRow> _rows = [];
    private readonly HashSet<string> _collapsedKeys = [];
    private string _filterQuery = string.Empty;
    private bool _isLoading = true;
    private string? _errorMessage;
    private bool _disposed;

    // Selection: the currently chosen file row (diff panel wiring lands in Stage 6 - Monaco).
    private GitChangesTreeRow? _selectedRow;

    private readonly HashSet<int> _mutatingRepositoryIds = [];

    protected override async Task OnInitializedAsync()
    {
        await LoadAsync();
    }

    protected override async Task OnParametersSetAsync()
    {
        if (_view != null && _view.WorkspaceId == WorkspaceId)
        {
            return;
        }

        await LoadAsync();
    }

    // Reads the persisted SQLite projection only - never sends an Agent command. Opening or reloading
    // this page must never trigger a status scan.
    private async Task LoadAsync()
    {
        _isLoading = true;
        _errorMessage = null;
        StateHasChanged();

        try
        {
            _workspace = await DbContext.Workspaces.AsNoTracking().FirstOrDefaultAsync(w => w.WorkspaceId == WorkspaceId);
            _view = await ReadService.GetWorkspaceAsync(WorkspaceId, CancellationToken.None);
            RebuildRows();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to load Git Changes for workspace {WorkspaceId}", WorkspaceId);
            _errorMessage = "Failed to load Changes.";
        }
        finally
        {
            _isLoading = false;
            StateHasChanged();
        }
    }

    private string MostRecentPersistedLabel()
    {
        var latest = _view?.Repositories.Select(r => r.PersistedAt).Where(t => t.HasValue).Select(t => t!.Value).OrderDescending().FirstOrDefault();
        return latest is { } value ? $" from {value.ToLocalTime():t}" : string.Empty;
    }

    private string? OfflineNotice => !AgentBridge.IsAgentConnected
        ? $"Agent offline • showing persisted state{(_view?.Repositories.Count > 0 ? MostRecentPersistedLabel() : string.Empty)}"
        : null;

    private void RebuildRows()
    {
        _rows = _view == null
            ? []
            : GitChangesTreeBuilder.Build(_view, _filterQuery, _collapsedKeys);
    }

    private void OnFilterChanged(string value)
    {
        _filterQuery = value;
        RebuildRows();
    }

    private void ToggleExpanded(GitChangesTreeRow row)
    {
        if (!row.HasChildren)
        {
            return;
        }

        if (!_collapsedKeys.Add(row.Key))
        {
            _collapsedKeys.Remove(row.Key);
        }

        RebuildRows();
    }

    private void SelectFile(GitChangesTreeRow row)
    {
        if (row.Kind != GitChangesTreeRowKind.File)
        {
            return;
        }

        _selectedRow = row;
        _ = LoadDiffAsync(row);
    }

    private bool IsMutating(int workspaceRepositoryId) => _mutatingRepositoryIds.Contains(workspaceRepositoryId);

    private async Task StageAsync(int workspaceRepositoryId, GitChangeOperationScope scope, IReadOnlyList<string> paths)
    {
        await RunMutationAsync(workspaceRepositoryId, async (root, wsName, repoName, repositoryId) =>
        {
            var result = await AgentClient.StageAsync(root, wsName, repoName, scope, paths, CancellationToken.None);
            await PersistMutationResultAsync(workspaceRepositoryId, repositoryId, result.Success, result.Snapshot, result.ErrorMessage);
        });
    }

    private async Task UnstageAsync(int workspaceRepositoryId, GitChangeOperationScope scope, IReadOnlyList<string> paths)
    {
        await RunMutationAsync(workspaceRepositoryId, async (root, wsName, repoName, repositoryId) =>
        {
            var result = await AgentClient.UnstageAsync(root, wsName, repoName, scope, paths, CancellationToken.None);
            await PersistMutationResultAsync(workspaceRepositoryId, repositoryId, result.Success, result.Snapshot, result.ErrorMessage);
        });
    }

    private async Task RunMutationAsync(int workspaceRepositoryId, Func<string, string, string, int, Task> action)
    {
        if (!AgentBridge.IsAgentConnected)
        {
            ToastService.ShowError("Agent not connected. Start GrayMoon.Agent and try again.");
            return;
        }

        if (!_mutatingRepositoryIds.Add(workspaceRepositoryId))
        {
            return; // A mutation for this repository is already in flight.
        }

        StateHasChanged();

        try
        {
            var resolved = await ResolveRepositoryAsync(workspaceRepositoryId);
            if (resolved == null)
            {
                ToastService.ShowError("Repository not found or workspace root is not configured.");
                return;
            }

            await action(resolved.Value.Root, resolved.Value.WorkspaceName, resolved.Value.RepositoryName, resolved.Value.RepositoryId);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Git Changes mutation failed for workspace-repository {WorkspaceRepositoryId}", workspaceRepositoryId);
            ToastService.ShowError("Operation failed. See logs for details.");
        }
        finally
        {
            _mutatingRepositoryIds.Remove(workspaceRepositoryId);
            StateHasChanged();
        }
    }

    /// <summary>
    /// Persists a mutation's returned snapshot through the same queue/handler used for Agent-pushed
    /// snapshots, so stage/unstage/commit never create a separate optimistic front-end truth - the tree
    /// always re-renders from the persisted SQLite projection, reloaded once the write completes.
    /// <paramref name="reload"/> is false for multi-repository fan-out, which reloads once after every
    /// repository's result has been persisted rather than once per repository.
    /// </summary>
    private async Task PersistMutationResultAsync(int workspaceRepositoryId, int repositoryId, bool success, GitChangeSnapshot? snapshot, string? errorMessage, bool reload = true)
    {
        if (!success)
        {
            ToastService.ShowError(errorMessage ?? "Operation failed.");
        }

        if (snapshot == null)
        {
            return;
        }

        WriteQueue.Enqueue(new GitChangesSnapshotNotification
        {
            WorkspaceId = WorkspaceId,
            RepositoryId = repositoryId,
            Snapshot = snapshot,
        });

        if (!reload)
        {
            return;
        }

        // The write queue processes on a background worker; give it a moment before reloading so the
        // page reflects the just-persisted state rather than racing the write.
        await Task.Delay(150);
        await LoadAsync();
    }

    private async Task<(string Root, string WorkspaceName, string RepositoryName, int RepositoryId)?> ResolveRepositoryAsync(int workspaceRepositoryId)
    {
        var link = await DbContext.WorkspaceRepositories
            .Include(l => l.Workspace)
            .Include(l => l.Repository)
            .FirstOrDefaultAsync(l => l.WorkspaceRepositoryId == workspaceRepositoryId);

        if (link?.Workspace == null || link.Repository == null)
        {
            return null;
        }

        var root = await WorkspaceService.GetRootPathForWorkspaceAsync(link.Workspace);
        return string.IsNullOrWhiteSpace(root) ? null : (root, link.Workspace.Name, link.Repository.RepositoryName, link.RepositoryId);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_hubConnection != null)
        {
            await _hubConnection.DisposeAsync();
        }
    }
}
