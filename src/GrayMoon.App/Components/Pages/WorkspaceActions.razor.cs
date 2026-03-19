using GrayMoon.App.Models;
using GrayMoon.App.Repositories;
using GrayMoon.App.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Options;

namespace GrayMoon.App.Components.Pages;

public sealed partial class WorkspaceActions : IDisposable
{
    [Parameter] public int WorkspaceId { get; set; }

    [Inject] private WorkspaceActionService ActionService { get; set; } = null!;
    [Inject] private GitHubActionsService GitHubActionsService { get; set; } = null!;
    [Inject] private WorkspaceRepository WorkspaceRepository { get; set; } = null!;
    [Inject] private IOptions<WorkspaceOptions> WorkspaceOptions { get; set; } = null!;
    [Inject] private IServiceScopeFactory ServiceScopeFactory { get; set; } = null!;
    [Inject] private NavigationManager NavigationManager { get; set; } = null!;
    [Inject] private ILogger<WorkspaceActions> Logger { get; set; } = null!;

    private int MaxConcurrency => Math.Max(1, WorkspaceOptions.Value.MaxParallelOperations);

    internal sealed class WorkspaceActionRow
    {
        public required WorkspaceRepositoryLink Link { get; set; }
        public required GitHubRepositoryEntry Repo { get; init; }

        /// <summary>Persisted or freshly-fetched aggregate action status. Null until first DB load or fetch.</summary>
        public ActionStatusInfo? Action { get; set; }

        /// <summary>True once the status has been verified against GitHub for the current branch.</summary>
        public bool IsVerified { get; set; }

        public bool IsRefreshing { get; set; }
        public bool RunInProgress { get; set; }
        public string? ErrorMessage { get; set; }
    }

    private Workspace? workspace;
    private List<WorkspaceActionRow> rows = [];
    private string? errorMessage;
    private bool isLoading = true;
    private bool isRefreshing;
    private bool isRerunningAll;
    private int _rerunTotal;
    private volatile int _rerunCompleted;
    private CancellationTokenSource _cts = new();
    private HubConnection? _hubConnection;
    private CancellationTokenSource? _syncDebounceCts;
    private bool _autoPollRunning;
    private const int SyncDebounceMs = 500;
    private const int AutoPollIntervalMs = 5000;

    internal bool HasFailedRows => rows.Any(r =>
        string.Equals(r.Action?.Status, "failed", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(r.Action?.BranchName, r.Link.BranchName, StringComparison.OrdinalIgnoreCase));

    private bool HasRunningRows => rows.Any(r =>
        string.Equals(r.Action?.Status, "running", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(r.Action?.BranchName, r.Link.BranchName, StringComparison.OrdinalIgnoreCase));

    internal string RerunAllOverlayMessage =>
        _rerunCompleted == 0
            ? "Re-running actions..."
            : $"Re-running {_rerunCompleted} of {_rerunTotal}";

    protected override async Task OnInitializedAsync()
    {
        await LoadWorkspaceAsync();
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender) return;

        if (!isLoading && rows.Count > 0)
            StartBackgroundRefresh();

        if (workspace != null && errorMessage == null)
        {
            _hubConnection = new HubConnectionBuilder()
                .WithUrl(NavigationManager.ToAbsoluteUri("/hubs/workspace-sync"))
                .WithAutomaticReconnect()
                .Build();

            _hubConnection.On<int>("WorkspaceSynced", async (workspaceId) =>
            {
                if (workspaceId != WorkspaceId) return;

                _syncDebounceCts?.Cancel();
                _syncDebounceCts?.Dispose();
                _syncDebounceCts = new CancellationTokenSource();
                var cts = _syncDebounceCts;
                try
                {
                    await Task.Delay(SyncDebounceMs, cts.Token);
                    await InvokeAsync(RefreshFromSyncAsync);
                }
                catch (OperationCanceledException) { /* debounced */ }
                finally
                {
                    if (cts == _syncDebounceCts)
                    {
                        _syncDebounceCts?.Dispose();
                        _syncDebounceCts = null;
                    }
                }
            });

            await _hubConnection.StartAsync();
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

    /// <summary>
    /// Called when WorkspaceSynced is received. Reloads workspace links from a fresh DB scope,
    /// updates branch names in-place, and triggers background fetches only for rows where the branch changed.
    /// </summary>
    private async Task RefreshFromSyncAsync()
    {
        try
        {
            await using var scope = ServiceScopeFactory.CreateAsyncScope();
            var repo = scope.ServiceProvider.GetRequiredService<WorkspaceRepository>();
            var freshWorkspace = await repo.GetByIdAsync(WorkspaceId);
            if (freshWorkspace == null) return;

            workspace = freshWorkspace;

            var freshLinks = freshWorkspace.Repositories
                .Where(l => l.Repository != null && l.Repository.Connector != null)
                .ToDictionary(l => l.RepositoryId);

            var rowsToRefresh = new List<WorkspaceActionRow>();

            foreach (var row in rows)
            {
                if (!freshLinks.TryGetValue(row.Repo.RepositoryId, out var freshLink)) continue;

                var branchChanged = !string.Equals(row.Link.BranchName, freshLink.BranchName, StringComparison.OrdinalIgnoreCase);
                row.Link = freshLink;

                if (branchChanged)
                {
                    // Branch mismatch → invalidate cached status so badge shows "none" immediately
                    row.Action = null;
                    row.IsVerified = false;
                    rowsToRefresh.Add(row);
                }
            }

            await InvokeAsync(StateHasChanged);

            foreach (var row in rowsToRefresh.Where(r => !string.IsNullOrWhiteSpace(r.Link.BranchName)))
            {
                _ = RefreshRowAsync(row, _cts.Token);
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error refreshing from sync for workspace {WorkspaceId}", WorkspaceId);
        }
    }

    private void StartBackgroundRefresh()
    {
        foreach (var row in rows.Where(r => !string.IsNullOrWhiteSpace(r.Link.BranchName)))
        {
            _ = RefreshRowAsync(row, _cts.Token);
        }
    }

    private void EnsureAutoPollRunning()
    {
        if (_autoPollRunning) return;
        _autoPollRunning = true;
        _ = AutoPollLoopAsync(_cts.Token);
    }

    private async Task AutoPollLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(AutoPollIntervalMs, cancellationToken);

                if (cancellationToken.IsCancellationRequested) break;

                var runningRows = rows
                    .Where(r =>
                        string.Equals(r.Action?.Status, "running", StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(r.Action?.BranchName, r.Link.BranchName, StringComparison.OrdinalIgnoreCase) &&
                        !string.IsNullOrWhiteSpace(r.Link.BranchName))
                    .ToList();

                if (runningRows.Count == 0) break;

                var tasks = runningRows.Select(row => RefreshRowAsync(row, cancellationToken)).ToList();
                await Task.WhenAll(tasks);
            }
        }
        catch (OperationCanceledException) { /* page disposed */ }
        finally
        {
            _autoPollRunning = false;
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

                if (string.Equals(info?.Status, "running", StringComparison.OrdinalIgnoreCase))
                    EnsureAutoPollRunning();
            }
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            Logger.LogError(ex, "Error refreshing action for {Repo}/{Branch}", row.Repo.RepositoryName, row.Link.BranchName);
            row.ErrorMessage = ex.Message;
            _ = ClearRowErrorAsync(row, row.ErrorMessage);
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

        row.ErrorMessage = null;
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
            row.ErrorMessage = ex.Message;
            _ = ClearRowErrorAsync(row, row.ErrorMessage);
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

        row.ErrorMessage = null;
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
            row.ErrorMessage = ex.Message;
            _ = ClearRowErrorAsync(row, row.ErrorMessage);
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

    internal async Task RerunAllFailedAsync()
    {
        var failedRows = rows
            .Where(r =>
                string.Equals(r.Action?.Status, "failed", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(r.Action?.BranchName, r.Link.BranchName, StringComparison.OrdinalIgnoreCase) &&
                (r.Action?.RunId ?? 0) > 0)
            .ToList();

        if (failedRows.Count == 0) return;

        _rerunTotal = failedRows.Count;
        _rerunCompleted = 0;
        isRerunningAll = true;
        await InvokeAsync(StateHasChanged);

        try
        {
            using var semaphore = new SemaphoreSlim(MaxConcurrency, MaxConcurrency);
            var tasks = failedRows.Select(async row =>
            {
                await semaphore.WaitAsync(_cts.Token);
                try
                {
                    var actionEntry = BuildActionEntry(row);
                    await GitHubActionsService.RerunWorkflowAsync(actionEntry);
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "Error re-running workflow for {Repo}", row.Repo.RepositoryName);
                }
                finally
                {
                    semaphore.Release();
                    Interlocked.Increment(ref _rerunCompleted);
                    await InvokeAsync(StateHasChanged);
                }
            });
            await Task.WhenAll(tasks);
        }
        finally
        {
            isRerunningAll = false;
            await InvokeAsync(StateHasChanged);
            // Refresh all rows so badges reflect the new run state
            _ = RefreshAllAsync();
        }
    }

    private async Task ClearRowErrorAsync(WorkspaceActionRow row, string? messageToClear)
    {
        await Task.Delay(5000);
        if (row.ErrorMessage == messageToClear)
        {
            row.ErrorMessage = null;
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
        _syncDebounceCts?.Cancel();
        _syncDebounceCts?.Dispose();
        _ = _hubConnection?.StopAsync();
        _ = _hubConnection?.DisposeAsync().AsTask();
    }
}
