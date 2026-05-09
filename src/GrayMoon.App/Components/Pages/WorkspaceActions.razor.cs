using System.Net;
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

    internal sealed class WorkflowActionLine
    {
        public ActionStatusInfo? Action { get; set; }
        public bool RunInProgress { get; set; }
    }

    internal sealed class WorkspaceActionRow
    {
        public required WorkspaceRepositoryLink Link { get; set; }
        public required GitHubRepositoryEntry Repo { get; init; }

        public List<WorkflowActionLine> WorkflowLines { get; set; } = [];

        public bool IsVerified { get; set; }
        public bool IsRefreshing { get; set; }
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

    /// <summary>After dispatch/rerun, GitHub may not list the new run immediately; refresh until we see running or exhaust attempts.</summary>
    private const int RunWorkflowVisibilityMaxAttempts = 10;

    private const int RunWorkflowVisibilityFirstDelayMs = 2000;
    private const int RunWorkflowVisibilityRetryDelayMs = 3000;

    internal bool HasFailedRows => rows.Any(row =>
        row.WorkflowLines.Any(line =>
            IsLineFailedForBranch(row, line)));

    internal int ErrorCount => rows.Count(r => !string.IsNullOrWhiteSpace(r.ErrorMessage));

    internal int FailedCount => rows.Sum(row => row.WorkflowLines.Count(line =>
        string.IsNullOrWhiteSpace(row.ErrorMessage) && IsLineFailedForBranch(row, line)));

    internal int RunningCount => rows.Sum(row => row.WorkflowLines.Count(line =>
        string.IsNullOrWhiteSpace(row.ErrorMessage) && IsLineRunningForBranch(row, line)));

    internal int SuccessCount => rows.Sum(row => row.WorkflowLines.Count(line =>
        string.IsNullOrWhiteSpace(row.ErrorMessage) && IsLineSuccessForBranch(row, line)));

    internal int NoneCount => rows.Sum(row => row.WorkflowLines.Count(line =>
        string.IsNullOrWhiteSpace(row.ErrorMessage) && IsLineNoneForBranch(row, line)));

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
                    var hasPersisted = persistedActions.TryGetValue(link.RepositoryId, out var persisted);
                    var branchMatches = hasPersisted &&
                        string.Equals(persisted?.BranchName, link.BranchName, StringComparison.OrdinalIgnoreCase);

                    List<WorkflowActionLine> workflowLines;
                    if (branchMatches && persisted != null && persisted.Workflows.Count > 0)
                    {
                        workflowLines = persisted.Workflows
                            .Select(w => new WorkflowActionLine { Action = w })
                            .ToList();
                    }
                    else
                    {
                        workflowLines = [new WorkflowActionLine()];
                    }

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
                        WorkflowLines = workflowLines,
                        IsVerified = branchMatches && hasPersisted
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
                    row.WorkflowLines = [new WorkflowActionLine()];
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
                        !string.IsNullOrWhiteSpace(r.Link.BranchName) &&
                        r.WorkflowLines.Any(line => IsLineRunningForBranch(r, line)))
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

            var list = await ActionService.FetchAndPersistAsync(
                row.Link.WorkspaceRepositoryId,
                row.Repo,
                row.Link.BranchName!,
                cancellationToken);

            if (!cancellationToken.IsCancellationRequested && list != null)
            {
                row.WorkflowLines = list
                    .Select(w => new WorkflowActionLine { Action = w })
                    .ToList();
                row.IsVerified = true;
                row.ErrorMessage = null;

                if (row.WorkflowLines.Any(line => IsLineRunningForBranch(row, line)))
                    EnsureAutoPollRunning();
            }
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            Logger.LogError(ex, "Error refreshing action for {Repo}/{Branch}", row.Repo.RepositoryName, row.Link.BranchName);
            row.ErrorMessage = GetFriendlyErrorMessage(ex);
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

    /// <summary>Refreshes the row until the given workflow shows as running with a run id, or max attempts (GitHub listing lag after dispatch/rerun).</summary>
    private async Task<bool> TryRefreshUntilWorkflowLineRunningAsync(
        WorkspaceActionRow row,
        long workflowId,
        string operationLabel,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= RunWorkflowVisibilityMaxAttempts; attempt++)
        {
            var delayMs = attempt == 1 ? RunWorkflowVisibilityFirstDelayMs : RunWorkflowVisibilityRetryDelayMs;
            try
            {
                await Task.Delay(delayMs, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return false;
            }

            await RefreshRowAsync(row, cancellationToken);

            if (!string.IsNullOrWhiteSpace(row.ErrorMessage))
            {
                Logger.LogWarning(
                    "{Operation}: refresh failed for row after attempt {Attempt}; stopping visibility retries",
                    operationLabel,
                    attempt);
                return false;
            }

            var wfLine = row.WorkflowLines.FirstOrDefault(l => l.Action?.WorkflowId == workflowId);
            if (wfLine != null
                && IsLineRunningForBranch(row, wfLine)
                && (wfLine.Action?.RunId ?? 0) > 0)
            {
                Logger.LogInformation(
                    "{Operation}: GitHub shows running workflow after attempt {Attempt}/{Max} WorkflowId={WorkflowId} RunId={RunId}",
                    operationLabel,
                    attempt,
                    RunWorkflowVisibilityMaxAttempts,
                    workflowId,
                    wfLine.Action!.RunId);
                return true;
            }

            if (attempt < RunWorkflowVisibilityMaxAttempts)
            {
                Logger.LogDebug(
                    "{Operation}: attempt {Attempt}/{Max} — workflow not running in API yet WorkflowId={WorkflowId}",
                    operationLabel,
                    attempt,
                    RunWorkflowVisibilityMaxAttempts,
                    workflowId);
            }
        }

        Logger.LogWarning(
            "{Operation}: no running workflow after {Max} refresh attempts WorkflowId={WorkflowId} (run may still be queued; grid will update on next poll)",
            operationLabel,
            RunWorkflowVisibilityMaxAttempts,
            workflowId);
        return false;
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

    internal async Task RerunWorkflowAsync(WorkspaceActionRow row, WorkflowActionLine line)
    {
        if (line.Action?.RunId == null) return;

        row.ErrorMessage = null;
        line.RunInProgress = true;
        await InvokeAsync(StateHasChanged);

        try
        {
            var actionEntry = BuildActionEntry(row, line);
            Logger.LogInformation(
                "GHA Re-run requested: WorkspaceId={WorkspaceId} Connector={Connector} {Owner}/{Repo} branch={Branch} workflow={WorkflowName} WorkflowId={WorkflowId} RunId={RunId}",
                WorkspaceId,
                row.Repo.ConnectorName,
                actionEntry.Owner,
                actionEntry.RepositoryName,
                actionEntry.HeadBranch,
                actionEntry.WorkflowName,
                actionEntry.WorkflowId,
                actionEntry.RunId);
            await GitHubActionsService.RerunWorkflowAsync(actionEntry);
            var rerunVisible = await TryRefreshUntilWorkflowLineRunningAsync(
                row,
                actionEntry.WorkflowId,
                "GHA Re-run",
                CancellationToken.None);
            Logger.LogInformation(
                "GHA Re-run completed (refresh phase): WorkspaceId={WorkspaceId} {Owner}/{Repo} workflow={WorkflowName} priorRunId={RunId} runningVisible={RunningVisible}",
                WorkspaceId,
                actionEntry.Owner,
                actionEntry.RepositoryName,
                actionEntry.WorkflowName,
                actionEntry.RunId,
                rerunVisible);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex,
                "GHA Re-run failed: WorkspaceId={WorkspaceId} {Owner}/{Repo} workflow={WorkflowName} RunId={RunId}",
                WorkspaceId,
                row.Repo.OrgName,
                row.Repo.RepositoryName,
                line.Action?.WorkflowName,
                line.Action?.RunId);
            row.ErrorMessage = GetFriendlyErrorMessage(ex);
            await InvokeAsync(StateHasChanged);
        }
        finally
        {
            line.RunInProgress = false;
            await InvokeAsync(StateHasChanged);
        }
    }

    internal async Task RunWorkflowAsync(WorkspaceActionRow row, WorkflowActionLine line)
    {
        if (line.Action?.WorkflowId == null) return;
        if (IsLineRunningForBranch(row, line)) return;

        row.ErrorMessage = null;
        line.RunInProgress = true;
        await InvokeAsync(StateHasChanged);

        try
        {
            var actionEntry = BuildActionEntry(row, line);
            Logger.LogInformation(
                "GHA Run (workflow_dispatch) requested: WorkspaceId={WorkspaceId} Connector={Connector} {Owner}/{Repo} branch={Branch} workflow={WorkflowName} WorkflowId={WorkflowId}",
                WorkspaceId,
                row.Repo.ConnectorName,
                actionEntry.Owner,
                actionEntry.RepositoryName,
                actionEntry.HeadBranch,
                actionEntry.WorkflowName,
                actionEntry.WorkflowId);
            await GitHubActionsService.RunWorkflowAsync(actionEntry);
            var runVisible = await TryRefreshUntilWorkflowLineRunningAsync(
                row,
                actionEntry.WorkflowId,
                "GHA Run",
                CancellationToken.None);
            Logger.LogInformation(
                "GHA Run completed (dispatch + refresh phase): WorkspaceId={WorkspaceId} {Owner}/{Repo} branch={Branch} workflow={WorkflowName} WorkflowId={WorkflowId} runningVisible={RunningVisible}",
                WorkspaceId,
                actionEntry.Owner,
                actionEntry.RepositoryName,
                actionEntry.HeadBranch,
                actionEntry.WorkflowName,
                actionEntry.WorkflowId,
                runVisible);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex,
                "GHA Run failed: WorkspaceId={WorkspaceId} {Owner}/{Repo} branch={Branch} workflow={WorkflowName} WorkflowId={WorkflowId}",
                WorkspaceId,
                row.Repo.OrgName,
                row.Repo.RepositoryName,
                row.Link.BranchName,
                line.Action?.WorkflowName,
                line.Action?.WorkflowId);
            row.ErrorMessage = GetFriendlyErrorMessage(ex);
            await InvokeAsync(StateHasChanged);
        }
        finally
        {
            line.RunInProgress = false;
            await InvokeAsync(StateHasChanged);
        }
    }

    private static GitHubActionEntry BuildActionEntry(WorkspaceActionRow row, WorkflowActionLine line) => new()
    {
        RunId = line.Action?.RunId ?? 0,
        WorkflowId = line.Action?.WorkflowId ?? 0,
        ConnectorName = row.Repo.ConnectorName,
        Owner = row.Repo.OrgName ?? string.Empty,
        RepositoryName = row.Repo.RepositoryName,
        WorkflowName = line.Action?.WorkflowName ?? string.Empty,
        Status = string.Empty,
        HeadBranch = row.Link.BranchName
    };

    internal async Task RerunAllFailedAsync()
    {
        var failedPairs = new List<(WorkspaceActionRow Row, WorkflowActionLine Line)>();
        foreach (var row in rows)
        {
            foreach (var line in row.WorkflowLines)
            {
                if (IsLineFailedForBranch(row, line) && (line.Action?.RunId ?? 0) > 0)
                    failedPairs.Add((row, line));
            }
        }

        if (failedPairs.Count == 0) return;

        Logger.LogInformation(
            "GHA Re-run all failed requested: WorkspaceId={WorkspaceId} count={Count}",
            WorkspaceId,
            failedPairs.Count);

        _rerunTotal = failedPairs.Count;
        _rerunCompleted = 0;
        isRerunningAll = true;
        await InvokeAsync(StateHasChanged);

        try
        {
            using var semaphore = new SemaphoreSlim(MaxConcurrency, MaxConcurrency);
            var tasks = failedPairs.Select(async pair =>
            {
                await semaphore.WaitAsync(_cts.Token);
                try
                {
                    var actionEntry = BuildActionEntry(pair.Row, pair.Line);
                    Logger.LogInformation(
                        "GHA Re-run all: invoking {Owner}/{Repo} workflow={WorkflowName} RunId={RunId}",
                        actionEntry.Owner,
                        actionEntry.RepositoryName,
                        actionEntry.WorkflowName,
                        actionEntry.RunId);
                    await GitHubActionsService.RerunWorkflowAsync(actionEntry);
                    Logger.LogInformation(
                        "GHA Re-run all: API ok {Owner}/{Repo} workflow={WorkflowName} RunId={RunId}",
                        actionEntry.Owner,
                        actionEntry.RepositoryName,
                        actionEntry.WorkflowName,
                        actionEntry.RunId);
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex,
                        "GHA Re-run all item failed: WorkspaceId={WorkspaceId} {Owner}/{Repo} RunId={RunId}",
                        WorkspaceId,
                        pair.Row.Repo.OrgName,
                        pair.Row.Repo.RepositoryName,
                        pair.Line.Action?.RunId);
                }
                finally
                {
                    semaphore.Release();
                    Interlocked.Increment(ref _rerunCompleted);
                    await InvokeAsync(StateHasChanged);
                }
            });
            await Task.WhenAll(tasks);
            Logger.LogInformation(
                "GHA Re-run all finished (API phase): WorkspaceId={WorkspaceId} attempted={Count}",
                WorkspaceId,
                failedPairs.Count);
        }
        finally
        {
            isRerunningAll = false;
            await InvokeAsync(StateHasChanged);
            _ = RefreshAllAsync();
        }
    }

    private static string GetFriendlyErrorMessage(Exception ex) =>
        ex switch
        {
            HttpRequestException { StatusCode: HttpStatusCode.Unauthorized } =>
                "Unauthorized (401). Check the connector token on the Connectors page.",
            HttpRequestException { StatusCode: HttpStatusCode.Forbidden } =>
                "Forbidden (403). The token does not have permission to access GitHub Actions.",
            HttpRequestException { StatusCode: HttpStatusCode.NotFound } =>
                "Not found (404). The repository or workflow was not found.",
            HttpRequestException { StatusCode: HttpStatusCode.UnprocessableEntity } http422 =>
                string.IsNullOrWhiteSpace(http422.Message)
                    ? "GitHub rejected the workflow request (422). It may not support manual runs on this branch, or required workflow inputs are missing."
                    : http422.Message,
            HttpRequestException { StatusCode: HttpStatusCode.TooManyRequests } =>
                "Rate limited (429). Too many requests to GitHub. Try again later.",
            HttpRequestException { StatusCode: HttpStatusCode.ServiceUnavailable } =>
                "GitHub service unavailable (503). Try again later.",
            HttpRequestException httpEx =>
                string.IsNullOrWhiteSpace(httpEx.Message)
                    ? "GitHub API request failed."
                    : httpEx.Message,
            OperationCanceledException =>
                "The operation was cancelled (for example, refresh was interrupted). Try Run again or use Refresh.",
            _ => ex.Message
        };

    internal static int GetStatusSortOrder(WorkspaceActionRow row)
    {
        if (!string.IsNullOrWhiteSpace(row.ErrorMessage)) return 0;
        var status = GetEffectiveStatusForSort(row);
        return status switch
        {
            "failed" => 1,
            "running" => 2,
            "success" => 3,
            _ => 4
        };
    }

    private static string? GetEffectiveStatusForSort(WorkspaceActionRow row)
    {
        var order = 4;
        string? worst = null;
        foreach (var line in row.WorkflowLines)
        {
            var a = line.Action;
            if (a == null || !string.Equals(a.BranchName, row.Link.BranchName, StringComparison.OrdinalIgnoreCase))
                continue;
            var o = a.Status switch
            {
                "failed" => 1,
                "running" => 2,
                "success" => 3,
                _ => 4
            };
            if (o < order)
            {
                order = o;
                worst = a.Status;
            }
        }

        return worst;
    }

    private static bool IsLineFailedForBranch(WorkspaceActionRow row, WorkflowActionLine line) =>
        line.Action != null &&
        string.Equals(line.Action.BranchName, row.Link.BranchName, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(line.Action.Status, "failed", StringComparison.OrdinalIgnoreCase);

    private static bool IsLineRunningForBranch(WorkspaceActionRow row, WorkflowActionLine line) =>
        line.Action != null &&
        string.Equals(line.Action.BranchName, row.Link.BranchName, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(line.Action.Status, "running", StringComparison.OrdinalIgnoreCase);

    private static bool IsLineSuccessForBranch(WorkspaceActionRow row, WorkflowActionLine line) =>
        line.Action != null &&
        string.Equals(line.Action.BranchName, row.Link.BranchName, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(line.Action.Status, "success", StringComparison.OrdinalIgnoreCase);

    private static bool IsLineNoneForBranch(WorkspaceActionRow row, WorkflowActionLine line) =>
        line.Action == null ||
        !string.Equals(line.Action.BranchName, row.Link.BranchName, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(line.Action.Status, "none", StringComparison.OrdinalIgnoreCase);

    internal static bool CanRerun(WorkspaceActionRow row, WorkflowActionLine line) =>
        IsLineFailedForBranch(row, line) && (line.Action?.RunId ?? 0) > 0;

    internal static bool CanRun(WorkspaceActionRow row, WorkflowActionLine line) =>
        (line.Action?.SupportsWorkflowDispatch ?? false) &&
        (line.Action?.WorkflowId ?? 0) > 0 &&
        !string.IsNullOrWhiteSpace(row.Link.BranchName);

    /// <summary>True when Run should be non-interactive: dispatch in flight or latest run still in progress on GitHub.</summary>
    internal static bool IsRunWorkflowBusy(WorkspaceActionRow row, WorkflowActionLine line) =>
        line.RunInProgress || IsLineRunningForBranch(row, line);

    /// <summary>GitHub Actions workflow page (not a specific run); uses persisted <see cref="ActionStatusInfo.WorkflowHtmlUrl"/> or builds from repo + workflow id.</summary>
    internal static string? GetWorkflowPageUrl(WorkspaceActionRow row, WorkflowActionLine line)
    {
        if (line.Action == null)
            return null;
        return RepositoryUrlHelper.BuildWorkflowPageUrl(
            line.Action.WorkflowHtmlUrl,
            row.Repo.CloneUrl,
            row.Repo.OrgName,
            row.Repo.RepositoryName,
            line.Action.WorkflowId ?? 0,
            line.Action.WorkflowPath,
            null);
    }

    internal static IEnumerable<WorkflowActionLine> LinesForDisplay(WorkspaceActionRow row)
    {
        if (row.WorkflowLines.Count > 0)
            return row.WorkflowLines;
        return [new WorkflowActionLine()];
    }

    internal static string GroupStripeClass(int groupIndex) =>
        (groupIndex % 2 == 0) ? "actions-group-even" : "actions-group-odd";

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
