using GrayMoon.App.Data;
using GrayMoon.App.Models;
using GrayMoon.App.Services;
using GrayMoon.App.Services.GitChanges;
using GrayMoon.Common.Git;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.JSInterop;

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
    [Inject] private IBackgroundJobService JobService { get; set; } = default!;
    [Inject] private WorkspaceGitChangesSelectionMemory SelectionMemory { get; set; } = default!;
    [Inject] private IJSRuntime Js { get; set; } = default!;

    private Workspace? _workspace;
    private WorkspaceGitChangesView? _view;
    private IReadOnlyList<GitChangesTreeRow> _rows = [];
    private readonly HashSet<string> _collapsedKeys = [];
    private string _filterQuery = string.Empty;
    private bool _isLoading = true;
    private string? _errorMessage;
    private bool _disposed;
    private bool _scrollSelectionIntoViewPending;

    // Selection: the currently chosen file row (diff panel wiring lands in Stage 6 - Monaco).
    private GitChangesTreeRow? _selectedRow;

    private readonly HashSet<int> _mutatingRepositoryIds = [];

    protected override Task OnInitializedAsync()
    {
        EnsureActivitySubscription();
        StartInitialLoadJob();
        return Task.CompletedTask;
    }

    protected override Task OnParametersSetAsync()
    {
        EnsureActivitySubscription();

        if (_view != null && _view.WorkspaceId == WorkspaceId)
        {
            return Task.CompletedTask;
        }

        StartInitialLoadJob();
        return Task.CompletedTask;
    }

    private Task? _initialLoadTask;

    /// <summary>
    /// Runs the initial (or workspace-switch) load inline behind the lightweight _isLoading flag instead
    /// of a background job, so opening this page (including re-opening it with a remembered file
    /// selection) never shows the BackgroundJobOverlay's "Loading changes..." LoadingOverlay - the tree
    /// itself renders as soon as the persisted projection is read, which is fast since it never sends an
    /// Agent command.
    /// </summary>
    private void StartInitialLoadJob()
    {
        if (_initialLoadTask is { IsCompleted: false })
        {
            return;
        }

        _initialLoadTask = LoadAsync();
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
            await ClearSelectionIfStaleAsync();
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

        // Restoring a remembered file selection re-fetches its diff from the Agent, which can be slow
        // (and, unlike the tree read above, is a real Agent command) - it must never gate _isLoading (and
        // therefore the Refresh button) or the tree render above. TryRestoreSelectionAsync only does work
        // when this page instance has no selection yet (first load / workspace switch); later reloads
        // triggered by Refresh or a mutation already have a selection and return immediately.
        if (_errorMessage == null)
        {
            await TryRestoreSelectionAsync();
        }
    }

    /// <summary>
    /// The Refresh button: a real, user-triggered rescan of every repository in the workspace (not just
    /// the ones currently showing changes), run behind the standard LoadingOverlay/terminal job so it's
    /// visibly a real operation. Relies on the same GitChangesUpdated -> LoadAsync pipeline used
    /// everywhere else for incremental per-repository updates as results stream in.
    /// </summary>
    private void ManualRefreshAsync()
    {
        if (!AgentBridge.IsAgentConnected)
        {
            ToastService.ShowError("Agent not connected. Start GrayMoon.Agent and try again.");
            return;
        }

        if (IsJobRunning)
        {
            ToastService.Show("Another Git Changes operation is already running.");
            return;
        }

        StartPageJob("Refreshing repositories...", (job, ct) =>
            Scanner.ScanWorkspaceAsync(WorkspaceId, ct, progress =>
                job.ReportProgress($"Refreshed {progress.Completed} of {progress.Total} repositories")));
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

    /// <summary>
    /// Clears the current file selection and diff (Monaco) whenever the selected file no longer has a
    /// matching change entry in the freshly-reloaded view - e.g. after a commit removes it from Staged/
    /// Changed. Runs off the underlying view rather than the rendered (filtered/collapsed) rows, so a
    /// collapsed folder or an active filter never wrongly clears a still-valid selection.
    /// </summary>
    private async Task ClearSelectionIfStaleAsync()
    {
        if (_selectedRow is not { Kind: GitChangesTreeRowKind.File } row)
        {
            return;
        }

        if (SelectionStillExists(row.WorkspaceRepositoryId, row.FilePath!, row.IsStagedSection))
        {
            return;
        }

        await ClearSelectionQuietlyAsync(clearMemory: true);
    }

    /// <summary>
    /// After a fresh page instance loads (SPA navigate-away-and-back), re-apply the circuit-scoped
    /// remembered file selection and reload its diff. Skipped when this page instance already has a
    /// selection (e.g. post-mutation reload) - ClearSelectionIfStaleAsync handles that path.
    /// Never fails the page load: if the file was committed away outside GrayMoon (or the diff cannot
    /// be loaded), selection and memory are cleared quietly.
    /// </summary>
    private async Task TryRestoreSelectionAsync()
    {
        try
        {
            if (_disposed || _selectedRow is { Kind: GitChangesTreeRowKind.File })
            {
                return;
            }

            if (!SelectionMemory.TryGet(WorkspaceId, out var remembered))
            {
                return;
            }

            if (!SelectionStillExists(remembered.WorkspaceRepositoryId, remembered.FilePath, remembered.IsStagedSection))
            {
                SelectionMemory.Clear(WorkspaceId);
                return;
            }

            EnsureAncestorsExpanded(remembered.FilePath, remembered.WorkspaceRepositoryId, remembered.IsStagedSection);

            var row = FindFileRow(remembered.WorkspaceRepositoryId, remembered.FilePath, remembered.IsStagedSection);
            if (row == null)
            {
                // Still in the view but not rendered (active filter) - keep memory for a later visit.
                return;
            }

            _selectedRow = row;
            _scrollSelectionIntoViewPending = true;
            await LoadDiffAsync(row);

            if (_disposed)
            {
                return;
            }

            // Diff failed (agent offline, path gone after an external commit, etc.) - drop auto-selection
            // so the page stays on the empty "Select a file" placeholder instead of a stuck error pane.
            if (_diffError != null || _selectedDiff == null)
            {
                await ClearSelectionQuietlyAsync(clearMemory: true);
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to restore Git Changes selection for workspace {WorkspaceId}", WorkspaceId);
            try
            {
                await ClearSelectionQuietlyAsync(clearMemory: true);
            }
            catch (Exception clearEx)
            {
                Logger.LogDebug(clearEx, "Failed to clear selection after restore failure");
            }
        }
    }

    private GitChangesTreeRow? FindFileRow(int workspaceRepositoryId, string filePath, bool isStagedSection) =>
        _rows.FirstOrDefault(r =>
            r.Kind == GitChangesTreeRowKind.File
            && r.WorkspaceRepositoryId == workspaceRepositoryId
            && r.FilePath == filePath
            && r.IsStagedSection == isStagedSection);

    /// <summary>
    /// Expands any collapsed section/repo/folder ancestors so a restored file row is present in _rows
    /// and can be scrolled into view.
    /// </summary>
    private void EnsureAncestorsExpanded(string filePath, int workspaceRepositoryId, bool isStagedSection)
    {
        var section = isStagedSection ? "staged" : "changed";
        var current = $"{section}/{workspaceRepositoryId}";
        var expanded = _collapsedKeys.Remove(section) | _collapsedKeys.Remove(current);

        var segments = filePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < segments.Length - 1; i++)
        {
            current = $"{current}/{segments[i]}";
            expanded |= _collapsedKeys.Remove(current);
        }

        if (expanded)
        {
            RebuildRows();
        }
    }

    private bool SelectionStillExists(int workspaceRepositoryId, string filePath, bool isStagedSection)
    {
        var repo = _view?.Repositories.FirstOrDefault(r => r.WorkspaceRepositoryId == workspaceRepositoryId);
        return repo != null && repo.Changes.Any(c =>
            c.Path == filePath && (isStagedSection ? c.IsStaged : c.IsChanged));
    }

    private async Task ClearSelectionQuietlyAsync(bool clearMemory)
    {
        _selectedRow = null;
        _selectedDiff = null;
        _diffError = null;
        _scrollSelectionIntoViewPending = false;

        if (clearMemory)
        {
            SelectionMemory.Clear(WorkspaceId);
        }

        if (_diffViewerRef != null)
        {
            await _diffViewerRef.ClearAsync();
        }
    }

    private async Task ScrollSelectionIntoViewIfPendingAsync()
    {
        if (!_scrollSelectionIntoViewPending || _disposed || _selectedRow is not { Kind: GitChangesTreeRowKind.File })
        {
            return;
        }

        _scrollSelectionIntoViewPending = false;

        try
        {
            await Js.InvokeVoidAsync("scrollSelectedGitChangesRowIntoView");
        }
        catch (JSDisconnectedException)
        {
            // Circuit gone - nothing to scroll.
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Failed to scroll restored Git Changes selection into view");
        }
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
        SelectionMemory.Set(WorkspaceId, new WorkspaceGitChangesSelectionMemory.Selection(
            row.WorkspaceRepositoryId, row.FilePath!, row.IsStagedSection));
        _ = LoadDiffAsync(row);
    }

    // While a page job (bulk stage/unstage, commit, manual refresh) is running, disable every action
    // button - not just the affected repository - since only one page job can run at a time anyway.
    private bool IsMutating(int workspaceRepositoryId) => _mutatingRepositoryIds.Contains(workspaceRepositoryId) || IsJobRunning;

    // File and folder scopes are fast, frequent clicks during diff review - keep them on the lightweight
    // inline indicator (_mutatingRepositoryIds) rather than the full LoadingOverlay. Whole-repository
    // scope stages/unstages every tracked and untracked file in one repository and can take a moment, so
    // it gets the same overlay+terminal treatment as commit and the section-wide bulk actions.
    private Task StageAsync(int workspaceRepositoryId, GitChangeOperationScope scope, IReadOnlyList<string> paths) =>
        scope == GitChangeOperationScope.Repository
            ? RunRepositoryScopedMutationJobAsync(workspaceRepositoryId, isStage: true)
            : RunMutationAsync(workspaceRepositoryId, async (root, wsName, repoName, repositoryId) =>
            {
                var result = await AgentClient.StageAsync(root, wsName, repoName, scope, paths, CancellationToken.None);
                await PersistMutationResultAsync(workspaceRepositoryId, repositoryId, result.Success, result.Snapshot, result.ErrorMessage);
            });

    private Task UnstageAsync(int workspaceRepositoryId, GitChangeOperationScope scope, IReadOnlyList<string> paths) =>
        scope == GitChangeOperationScope.Repository
            ? RunRepositoryScopedMutationJobAsync(workspaceRepositoryId, isStage: false)
            : RunMutationAsync(workspaceRepositoryId, async (root, wsName, repoName, repositoryId) =>
            {
                var result = await AgentClient.UnstageAsync(root, wsName, repoName, scope, paths, CancellationToken.None);
                await PersistMutationResultAsync(workspaceRepositoryId, repositoryId, result.Success, result.Snapshot, result.ErrorMessage);
            });

    private Task RunRepositoryScopedMutationJobAsync(int workspaceRepositoryId, bool isStage)
    {
        if (!AgentBridge.IsAgentConnected)
        {
            ToastService.ShowError("Agent not connected. Start GrayMoon.Agent and try again.");
            return Task.CompletedTask;
        }

        if (IsJobRunning)
        {
            return Task.CompletedTask;
        }

        StartPageJob(
            isStage ? "Staging repository..." : "Unstaging repository...",
            async (job, ct) =>
            {
                var resolved = await ResolveRepositoryAsync(workspaceRepositoryId);
                if (resolved == null)
                {
                    ToastService.ShowError("Repository not found or workspace root is not configured.");
                    return;
                }

                var result = isStage
                    ? await AgentClient.StageAsync(resolved.Value.Root, resolved.Value.WorkspaceName, resolved.Value.RepositoryName, GitChangeOperationScope.Repository, [], ct)
                    : await AgentClient.UnstageAsync(resolved.Value.Root, resolved.Value.WorkspaceName, resolved.Value.RepositoryName, GitChangeOperationScope.Repository, [], ct);

                // reload:false - StartPageJob's own ReloadOnSuccess (properly dispatcher-marshalled via
                // InvokeAsync) does the final LoadAsync() once this job body returns; calling LoadAsync's
                // StateHasChanged directly from here would run off the Blazor circuit's sync context.
                await PersistMutationResultAsync(workspaceRepositoryId, resolved.Value.RepositoryId, result.Success, result.Snapshot, result.ErrorMessage, reload: false);

                // Give the write queue a moment to flush before the job's own reload runs.
                await Task.Delay(150, ct);
            });

        return Task.CompletedTask;
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
        ReleaseActivitySubscription();

        if (_hubConnection != null)
        {
            await _hubConnection.DisposeAsync();
        }
    }
}
