using GrayMoon.App.Models;
using GrayMoon.App.Repositories;
using GrayMoon.App.Services;
using Microsoft.AspNetCore.Components;

namespace GrayMoon.App.Components.Pages;

public sealed partial class WorkspaceActions : IDisposable
{
    [Parameter] public int WorkspaceId { get; set; }

    [Inject] private WorkspaceActionService ActionService { get; set; } = null!;
    [Inject] private GitHubActionsService GitHubActionsService { get; set; } = null!;
    [Inject] private WorkspaceRepository WorkspaceRepository { get; set; } = null!;
    [Inject] private ILogger<WorkspaceActions> Logger { get; set; } = null!;

    internal sealed class WorkspaceActionRow
    {
        public required WorkspaceRepositoryLink Link { get; init; }
        public required GitHubRepositoryEntry Repo { get; init; }

        /// <summary>Persisted or freshly-fetched aggregate action status. Null until first DB load or fetch.</summary>
        public ActionStatusInfo? Action { get; set; }

        /// <summary>True once the status has been verified against GitHub for the current branch.</summary>
        public bool IsVerified { get; set; }

        public bool IsRefreshing { get; set; }
        public bool RunInProgress { get; set; }
        public string? RunError { get; set; }
    }

    private Workspace? workspace;
    private List<WorkspaceActionRow> rows = [];
    private string? errorMessage;
    private bool isLoading = true;
    private bool isRefreshing;
    private CancellationTokenSource _cts = new();

    protected override async Task OnInitializedAsync()
    {
        await LoadWorkspaceAsync();
    }

    protected override void OnAfterRender(bool firstRender)
    {
        if (firstRender && !isLoading && rows.Count > 0)
        {
            StartBackgroundRefresh();
        }
    }

    private async Task LoadWorkspaceAsync()
    {
        isLoading = true;
        errorMessage = null;

        try
        {
            workspace = await WorkspaceRepository.GetByIdAsync(WorkspaceId);
            if (workspace == null)
            {
                errorMessage = "Workspace not found.";
                rows = [];
                return;
            }

            var persistedActions = await ActionService.GetPersistedActionsForWorkspaceAsync(WorkspaceId);

            rows = workspace.Repositories
                .Where(link => link.Repository != null && link.Repository.Connector != null)
                .OrderBy(link => link.Repository!.RepositoryName)
                .Select(link =>
                {
                    var hasPersisted = persistedActions.TryGetValue(link.RepositoryId, out var info);
                    var branchMatches = hasPersisted &&
                        string.Equals(info?.BranchName, link.BranchName, StringComparison.OrdinalIgnoreCase);
                    return new WorkspaceActionRow
                    {
                        Link = link,
                        Repo = new GitHubRepositoryEntry
                        {
                            RepositoryId = link.Repository!.RepositoryId,
                            ConnectorName = link.Repository.Connector?.ConnectorName ?? string.Empty,
                            OrgName = link.Repository.OrgName,
                            RepositoryName = link.Repository.RepositoryName,
                            Visibility = link.Repository.Visibility,
                            CloneUrl = link.Repository.CloneUrl
                        },
                        // Only use persisted status if it is for the current branch
                        Action = branchMatches ? info : null,
                        IsVerified = branchMatches
                    };
                })
                .ToList();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error loading workspace {WorkspaceId}", WorkspaceId);
            errorMessage = "Failed to load workspace. Please try again later.";
            rows = [];
        }
        finally
        {
            isLoading = false;
        }
    }

    private void StartBackgroundRefresh()
    {
        foreach (var row in rows.Where(r => !string.IsNullOrWhiteSpace(r.Link.BranchName)))
        {
            _ = RefreshRowAsync(row, _cts.Token);
        }
    }

    private async Task RefreshRowAsync(WorkspaceActionRow row, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(row.Link.BranchName)) return;

        try
        {
            row.IsRefreshing = true;

            var info = await ActionService.FetchAndPersistAsync(
                row.Link.WorkspaceRepositoryId,
                row.Repo,
                row.Link.BranchName!,
                cancellationToken);

            if (!cancellationToken.IsCancellationRequested)
            {
                row.Action = info;
                row.IsVerified = true;
            }
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            Logger.LogError(ex, "Error refreshing action for {Repo}/{Branch}", row.Repo.RepositoryName, row.Link.BranchName);
        }
        finally
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                row.IsRefreshing = false;
                await InvokeAsync(StateHasChanged);
            }
        }
    }

    internal async Task RefreshAllAsync()
    {
        if (workspace == null || rows.Count == 0) return;

        isRefreshing = true;
        await InvokeAsync(StateHasChanged);

        try
        {
            _cts.Cancel();
            _cts.Dispose();
            _cts = new CancellationTokenSource();

            var token = _cts.Token;
            var tasks = rows
                .Where(r => !string.IsNullOrWhiteSpace(r.Link.BranchName))
                .Select(row => RefreshRowAsync(row, token))
                .ToList();

            await Task.WhenAll(tasks);
        }
        finally
        {
            isRefreshing = false;
            await InvokeAsync(StateHasChanged);
        }
    }

    internal async Task RerunWorkflowAsync(WorkspaceActionRow row)
    {
        if (row.Action?.RunId == null) return;

        row.RunError = null;
        row.RunInProgress = true;
        await InvokeAsync(StateHasChanged);

        try
        {
            var actionEntry = BuildActionEntry(row);
            await GitHubActionsService.RerunWorkflowAsync(actionEntry);
            await RefreshRowAsync(row, _cts.Token);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error re-running workflow for {Repo}", row.Repo.RepositoryName);
            row.RunError = ex.Message;
            _ = ClearRowErrorAsync(row, row.RunError);
            await InvokeAsync(StateHasChanged);
        }
        finally
        {
            row.RunInProgress = false;
            await InvokeAsync(StateHasChanged);
        }
    }

    internal async Task RunWorkflowAsync(WorkspaceActionRow row)
    {
        if (row.Action?.WorkflowId == null) return;

        row.RunError = null;
        row.RunInProgress = true;
        await InvokeAsync(StateHasChanged);

        try
        {
            var actionEntry = BuildActionEntry(row);
            await GitHubActionsService.RunWorkflowAsync(actionEntry);
            // Brief delay so GitHub registers the new run before we poll
            await Task.Delay(2000);
            await RefreshRowAsync(row, _cts.Token);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error running workflow for {Repo}", row.Repo.RepositoryName);
            row.RunError = ex.Message;
            _ = ClearRowErrorAsync(row, row.RunError);
            await InvokeAsync(StateHasChanged);
        }
        finally
        {
            row.RunInProgress = false;
            await InvokeAsync(StateHasChanged);
        }
    }

    private static GitHubActionEntry BuildActionEntry(WorkspaceActionRow row) => new()
    {
        RunId = row.Action?.RunId ?? 0,
        WorkflowId = row.Action?.WorkflowId ?? 0,
        ConnectorName = row.Repo.ConnectorName,
        Owner = row.Repo.OrgName ?? string.Empty,
        RepositoryName = row.Repo.RepositoryName,
        WorkflowName = row.Action?.WorkflowName ?? string.Empty,
        Status = string.Empty,
        HeadBranch = row.Link.BranchName
    };

    private async Task ClearRowErrorAsync(WorkspaceActionRow row, string? messageToClear)
    {
        await Task.Delay(5000);
        if (row.RunError == messageToClear)
        {
            row.RunError = null;
            await InvokeAsync(StateHasChanged);
        }
    }

    internal static int GetStatusSortOrder(WorkspaceActionRow row)
    {
        var status = GetEffectiveStatusForSort(row);
        return status switch
        {
            "failed"  => 0,
            "running" => 1,
            "success" => 2,
            _         => 3  // none or unknown
        };
    }

    private static string? GetEffectiveStatusForSort(WorkspaceActionRow row)
    {
        if (row.Action == null) return null;
        if (!string.Equals(row.Action.BranchName, row.Link.BranchName, StringComparison.OrdinalIgnoreCase))
            return null;
        return row.Action.Status;
    }

    internal static bool CanRerun(WorkspaceActionRow row) =>
        string.Equals(row.Action?.Status, "failed", StringComparison.OrdinalIgnoreCase) &&
        (row.Action?.RunId ?? 0) > 0;

    internal static bool CanRun(WorkspaceActionRow row) =>
        (row.Action?.WorkflowId ?? 0) > 0 &&
        !string.IsNullOrWhiteSpace(row.Link.BranchName);

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}
