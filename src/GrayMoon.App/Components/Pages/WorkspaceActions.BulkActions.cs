using GrayMoon.App.Models;
using Microsoft.AspNetCore.Components.Web;

namespace GrayMoon.App.Components.Pages;

public sealed partial class WorkspaceActions
{
    internal void ToggleRerunMenu()
    {
        _rerunMenuOpen = !_rerunMenuOpen;
        StateHasChanged();
    }

    internal void CloseRerunMenu()
    {
        _rerunMenuOpen = false;
        StateHasChanged();
    }

    internal async Task HandleRerunAllClick()
    {
        CloseRerunMenu();
        await RerunAllFailedAsync();
    }

    internal async Task HandleRerunFailedJobsOnlyClick()
    {
        CloseRerunMenu();
        await RerunAllFailedJobsOnlyAsync();
    }

    internal async Task HandleRunAgainAllClick()
    {
        CloseRerunMenu();
        await RunAgainAllFailedAsync();
    }

    internal bool IsRerunLineMenuOpen(WorkflowActionLine line)
    {
        var key = GetLineLatchKey(line);
        return key > 0 && _openRerunLineMenuKey == key;
    }

    /// <summary>Uses the click's viewport coordinates (rather than the trigger button's position) so the fixed-position menu renders correctly regardless of the grid's scroll offset.</summary>
    internal void ToggleRerunLineMenu(WorkflowActionLine line, MouseEventArgs args)
    {
        var key = GetLineLatchKey(line);
        if (key <= 0) return;

        if (_openRerunLineMenuKey == key)
        {
            _openRerunLineMenuKey = null;
            StateHasChanged();
            return;
        }

        _rerunLineMenuTop = args.ClientY + 4;
        _rerunLineMenuLeft = args.ClientX;
        _openRerunLineMenuKey = key;
        StateHasChanged();
    }

    internal void CloseRerunLineMenu()
    {
        _openRerunLineMenuKey = null;
        StateHasChanged();
    }

    internal async Task HandleRunAgainClick(WorkspaceActionRow row, WorkflowActionLine line)
    {
        CloseRerunLineMenu();
        await RunWorkflowAsync(row, line);
    }

    /// <summary>Re-runs every failed workflow of the grid.</summary>
    internal Task RerunAllFailedAsync() =>
        RunBulkAsync(
            SelectRerunnable(rows, _isFeatureContext, IncludeAiWorkflows),
            "Re-running", "Re-run all", "Re-ran", scope: null,
            entry => GitHubActionsService.RerunWorkflowAsync(entry));

    /// <summary>Re-runs only the failed jobs of every failed workflow of the grid.</summary>
    internal Task RerunAllFailedJobsOnlyAsync() =>
        RunBulkAsync(
            SelectRerunnable(rows, _isFeatureContext, IncludeAiWorkflows),
            "Re-running", "Re-run failed jobs only", "Re-ran", scope: null,
            entry => GitHubActionsService.RerunFailedJobsOnlyAsync(entry));

    /// <summary>Bulk "Run again": dispatches a new run (latest commit on the selected branch) for every failed workflow that supports workflow_dispatch, skipping any that don't.</summary>
    internal Task RunAgainAllFailedAsync() =>
        RunBulkAsync(
            SelectRunAgain(rows, _isFeatureContext, IncludeAiWorkflows),
            "Starting", "Run again all", "Started", scope: null,
            entry => GitHubActionsService.RunWorkflowAsync(entry));

    /// <summary>
    /// The one bulk loop behind the page header actions and the group header menu: shows the overlay counter, calls
    /// GitHub for each workflow with bounded concurrency, and always reports the outcome. A workflow that could not be
    /// started leaves its error on the repository row and is named in the closing toast; the others still run.
    /// The overlay then waits, with Abort, until GitHub lists the started runs.
    /// Called from a Blazor event handler and stays on the dispatcher, so row fields are only written there.
    /// </summary>
    private async Task RunBulkAsync(
        IReadOnlyList<(WorkspaceActionRow Row, WorkflowActionLine Line)> pairs,
        string verb,
        string logName,
        string doneVerb,
        string? scope,
        Func<GitHubActionEntry, Task> invoke,
        bool waitUntilStopped = false)
    {
        if (pairs.Count == 0 || isRerunningAll) return;

        Logger.LogInformation(
            "GHA {Operation} requested: WorkspaceId={WorkspaceId} scope={Scope} count={Count}",
            logName,
            WorkspaceId,
            scope ?? "all",
            pairs.Count);

        _bulkOperationVerb = verb;
        _rerunTotal = pairs.Count;
        _rerunCompleted = 0;
        isRerunningAll = true;
        await InvokeAsync(StateHasChanged);

        IReadOnlyList<(WorkspaceActionRow Row, WorkflowActionLine Line)> failed = [];
        var interrupted = false;
        try
        {
            failed = await InvokeBulkAsync(
                pairs,
                async (row, line) =>
                {
                    var actionEntry = BuildActionEntry(row, line);
                    Logger.LogInformation(
                        "GHA {Operation}: invoking {Owner}/{Repo} branch={Branch} workflow={WorkflowName} WorkflowId={WorkflowId} RunId={RunId}",
                        logName,
                        actionEntry.Owner,
                        actionEntry.RepositoryName,
                        actionEntry.HeadBranch,
                        actionEntry.WorkflowName,
                        actionEntry.WorkflowId,
                        actionEntry.RunId);
                    await invoke(actionEntry);
                },
                MaxConcurrency,
                (pair, ex) => Logger.LogError(ex,
                    "GHA {Operation} item failed: WorkspaceId={WorkspaceId} {Owner}/{Repo} WorkflowId={WorkflowId} RunId={RunId}",
                    logName,
                    WorkspaceId,
                    pair.Row.Repo.OrgName,
                    pair.Row.Repo.RepositoryName,
                    pair.Line.Action?.WorkflowId,
                    pair.Line.Action?.RunId),
                async () =>
                {
                    Interlocked.Increment(ref _rerunCompleted);
                    if (!_disposed)
                        await InvokeAsync(StateHasChanged);
                },
                _cts.Token);

            Logger.LogInformation(
                "GHA {Operation} finished (API phase): WorkspaceId={WorkspaceId} attempted={Count} failed={Failed}",
                logName,
                WorkspaceId,
                pairs.Count,
                failed.Count);
        }
        catch (OperationCanceledException)
        {
            // The page was disposed, or a refresh replaced the token, before every workflow was reached.
            interrupted = true;
        }

        // The overlay stays up until GitHub lists the runs, so the grid shows them running (and the live
        // monitoring starts) when it closes. Abort ends only this wait; what was started keeps running.
        var notListed = 0;
        var waitAborted = false;
        try
        {
            if (!interrupted && !_disposed)
            {
                var started = pairs.Where(pair => !failed.Contains(pair)).Select(pair => StartedRun.From(pair.Row, pair.Line)).ToList();
                if (started.Count > 0)
                {
                    using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                    _bulkWaitCts = waitCts;
                    _rerunTotal = started.Count;
                    _rerunCompleted = 0;
                    _bulkWaiting = true;
                    await InvokeAsync(StateHasChanged);
                    try
                    {
                        notListed = await WaitForStartedRunsAsync(started, waitUntilStopped ? IsRunStopped : IsRunPickedUp, waitCts.Token);
                        waitAborted = waitCts.IsCancellationRequested;
                    }
                    finally
                    {
                        _bulkWaitCts = null;
                    }

                    if (!_disposed)
                        FollowUpStartedRuns(started, waitUntilStopped);
                    if (waitUntilStopped)
                        notListed = 0; // a run that is still stopping stays visible as running in the grid
                }
            }
        }
        catch (Exception ex) when (ex is ObjectDisposedException or OperationCanceledException)
        {
            // The page went away during the wait.
        }
        finally
        {
            _bulkWaiting = false;
            isRerunningAll = false;
        }

        if (_disposed) return;

        if (interrupted)
            ToastService.ShowError($"{verb} workflows was interrupted. Refresh to see what was started.");
        else if (failed.Count > 0)
            ToastService.ShowError(BulkOutcomeMessage(doneVerb, pairs.Count, failed.Select(pair => pair.Row.Repo.RepositoryName).ToList(), scope, waitAborted ? 0 : notListed));
        else
            ToastService.Show(BulkOutcomeMessage(doneVerb, pairs.Count, [], scope, waitAborted ? 0 : notListed));

        await InvokeAsync(StateHasChanged);
    }

    /// <summary>Ends the wait for GitHub to list the started runs (the overlay's Abort). Nothing that was started is cancelled.</summary>
    internal void AbortBulkWait()
    {
        try
        {
            _bulkWaitCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The wait ended on its own in the same moment.
        }
    }

    /// <summary>What a workflow line looked like when a bulk action started it, to tell when GitHub lists the new run.</summary>
    internal sealed record StartedRun(WorkspaceActionRow Row, long WorkflowId, long RunId, DateTimeOffset? UpdatedAt)
    {
        public static StartedRun From(WorkspaceActionRow row, WorkflowActionLine line) =>
            new(row, line.Action?.WorkflowId ?? 0, line.Action?.RunId ?? 0, line.Action?.UpdatedAt);
    }

    /// <summary>
    /// True when the row shows the run a bulk action started: the workflow is running, or it already shows another
    /// run or a later update than before (a run that finished between two refreshes).
    /// </summary>
    internal static bool IsRunPickedUp(StartedRun started)
    {
        var row = started.Row;
        var line = started.WorkflowId > 0
            ? row.WorkflowLines.FirstOrDefault(l => l.Action?.WorkflowId == started.WorkflowId)
            : row.WorkflowLines.FirstOrDefault(l => l.Action?.RunId == started.RunId);
        var action = line?.Action;
        if (action == null || (action.RunId ?? 0) <= 0
            || !string.Equals(action.BranchName, row.CiBranchName, StringComparison.OrdinalIgnoreCase))
            return false;

        return IsLineRunningForBranch(row, line!) || action.RunId != started.RunId || action.UpdatedAt != started.UpdatedAt;
    }

    /// <summary>True when the row no longer shows the run a bulk Abort cancelled as running.</summary>
    internal static bool IsRunStopped(StartedRun started)
    {
        var row = started.Row;
        var line = row.WorkflowLines.FirstOrDefault(l => l.Action?.RunId == started.RunId)
            ?? (started.WorkflowId > 0 ? row.WorkflowLines.FirstOrDefault(l => l.Action?.WorkflowId == started.WorkflowId) : null);
        return line == null || !IsLineRunningForBranch(row, line);
    }

    /// <summary>
    /// Refreshes the started rows until each run is listed, the wait is aborted, or the attempts run out. A refresh
    /// that finds a running workflow starts the auto-poll, so the rows keep updating after the overlay closes.
    /// Returns how many runs are still not listed.
    /// </summary>
    private async Task<int> WaitForStartedRunsAsync(IReadOnlyList<StartedRun> started, Func<StartedRun, bool> isSettled, CancellationToken cancellationToken)
    {
        using var semaphore = new SemaphoreSlim(MaxConcurrency, MaxConcurrency);
        for (var attempt = 1; attempt <= RunWorkflowVisibilityMaxAttempts; attempt++)
        {
            try
            {
                await Task.Delay(attempt == 1 ? RunWorkflowVisibilityFirstDelayMs : RunWorkflowVisibilityRetryDelayMs, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            var pendingRows = started.Where(run => !isSettled(run)).Select(run => run.Row).Distinct().ToList();
            await Task.WhenAll(pendingRows.Select(row => RefreshRowThrottledAsync(row, semaphore, cancellationToken)));

            if (_disposed) break;
            _rerunCompleted = started.Count(isSettled);
            await InvokeAsync(StateHasChanged);
            if (_rerunCompleted == started.Count || cancellationToken.IsCancellationRequested) break;
        }

        // A refresh cancelled by Abort leaves its row marked as refreshing.
        foreach (var row in started.Select(run => run.Row).Distinct())
            row.IsRefreshing = false;

        return started.Count(run => !isSettled(run));
    }

    /// <summary>After an aborted or exhausted wait: keep looking, out of sight, for the runs GitHub has not listed yet.</summary>
    private void FollowUpStartedRuns(IReadOnlyList<StartedRun> started, bool waitUntilStopped)
    {
        var token = _cts.Token;
        if (!waitUntilStopped)
        {
            foreach (var row in started.Where(run => !IsRunPickedUp(run)).Select(run => run.Row).Distinct())
                _ = TryRefreshUntilAnyWorkflowRunningAsync(row, token);
        }

        if (rows.Any(row => row.WorkflowLines.Any(line => IsLineRunningForBranch(row, line))))
            EnsureAutoPollRunning();
    }

    /// <summary>
    /// Calls <paramref name="invoke"/> for each workflow, at most <paramref name="maxConcurrency"/> at a time. A
    /// workflow that throws gets its error on the repository row and is returned; it never stops the others.
    /// </summary>
    internal static async Task<IReadOnlyList<(WorkspaceActionRow Row, WorkflowActionLine Line)>> InvokeBulkAsync(
        IReadOnlyList<(WorkspaceActionRow Row, WorkflowActionLine Line)> pairs,
        Func<WorkspaceActionRow, WorkflowActionLine, Task> invoke,
        int maxConcurrency,
        Action<(WorkspaceActionRow Row, WorkflowActionLine Line), Exception>? onItemFailed = null,
        Func<Task>? onItemCompleted = null,
        CancellationToken cancellationToken = default)
    {
        var failed = new List<(WorkspaceActionRow Row, WorkflowActionLine Line)>();
        using var semaphore = new SemaphoreSlim(maxConcurrency, maxConcurrency);
        var tasks = pairs.Select(async pair =>
        {
            await semaphore.WaitAsync(cancellationToken);
            try
            {
                await invoke(pair.Row, pair.Line);
            }
            catch (Exception ex)
            {
                pair.Row.ErrorMessage = GetFriendlyErrorMessage(ex);
                lock (failed)
                    failed.Add(pair);
                onItemFailed?.Invoke(pair, ex);
            }
            finally
            {
                semaphore.Release();
                if (onItemCompleted != null)
                    await onItemCompleted();
            }
        }).ToList();

        // WhenAll completes only when every item has, so a cancelled wait never leaves an item using the disposed semaphore.
        await Task.WhenAll(tasks);

        return pairs.Where(pair => failed.Contains(pair)).ToList();
    }

    /// <summary>The closing toast of a bulk action, for example "Started 7 of 8 workflows in Level 2. Failed: Api.".</summary>
    internal static string BulkOutcomeMessage(string doneVerb, int total, IReadOnlyList<string> failedRepositoryNames, string? scope, int notListedCount = 0)
    {
        var where = string.IsNullOrEmpty(scope) ? string.Empty : $" in {scope}";
        var notListed = notListedCount > 0 ? $" GitHub does not show {notListedCount} of them yet." : string.Empty;
        if (failedRepositoryNames.Count == 0)
            return $"{doneVerb} {total} {(total == 1 ? "workflow" : "workflows")}{where}.{notListed}";

        const int maxNames = 3;
        var names = failedRepositoryNames.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var listed = string.Join(", ", names.Take(maxNames));
        if (names.Count > maxNames)
            listed += $" and {names.Count - maxNames} more";

        return $"{doneVerb} {total - failedRepositoryNames.Count} of {total} workflows{where}. Failed: {listed}.{notListed}";
    }
}
