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

    internal async Task RerunAllFailedAsync()
    {
        var failedPairs = new List<(WorkspaceActionRow Row, WorkflowActionLine Line)>();
        foreach (var row in rows)
        {
            foreach (var line in row.WorkflowLines)
            {
                if (IsLineIncludedWhenAiFiltered(line) && IsLineFailedForBranch(row, line) && (line.Action?.RunId ?? 0) > 0)
                    failedPairs.Add((row, line));
            }
        }

        if (failedPairs.Count == 0) return;

        Logger.LogInformation(
            "GHA Re-run all failed requested: WorkspaceId={WorkspaceId} count={Count}",
            WorkspaceId,
            failedPairs.Count);

        _bulkOperationVerb = "Re-running";
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

    internal async Task RerunAllFailedJobsOnlyAsync()
    {
        var failedPairs = new List<(WorkspaceActionRow Row, WorkflowActionLine Line)>();
        foreach (var row in rows)
        {
            foreach (var line in row.WorkflowLines)
            {
                if (IsLineIncludedWhenAiFiltered(line) && IsLineFailedForBranch(row, line) && (line.Action?.RunId ?? 0) > 0)
                    failedPairs.Add((row, line));
            }
        }

        if (failedPairs.Count == 0) return;

        Logger.LogInformation(
            "GHA Re-run failed jobs only requested: WorkspaceId={WorkspaceId} count={Count}",
            WorkspaceId,
            failedPairs.Count);

        _bulkOperationVerb = "Re-running";
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
                        "GHA Re-run failed jobs only: invoking {Owner}/{Repo} workflow={WorkflowName} RunId={RunId}",
                        actionEntry.Owner,
                        actionEntry.RepositoryName,
                        actionEntry.WorkflowName,
                        actionEntry.RunId);
                    await GitHubActionsService.RerunFailedJobsOnlyAsync(actionEntry);
                    Logger.LogInformation(
                        "GHA Re-run failed jobs only: API ok {Owner}/{Repo} workflow={WorkflowName} RunId={RunId}",
                        actionEntry.Owner,
                        actionEntry.RepositoryName,
                        actionEntry.WorkflowName,
                        actionEntry.RunId);
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex,
                        "GHA Re-run failed jobs only item failed: WorkspaceId={WorkspaceId} {Owner}/{Repo} RunId={RunId}",
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
                "GHA Re-run failed jobs only finished (API phase): WorkspaceId={WorkspaceId} attempted={Count}",
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

    /// <summary>Bulk "Run again": dispatches a new run (latest commit on the selected branch) for every failed workflow that supports workflow_dispatch, skipping any that don't.</summary>
    internal async Task RunAgainAllFailedAsync()
    {
        var eligiblePairs = new List<(WorkspaceActionRow Row, WorkflowActionLine Line)>();
        foreach (var row in rows)
        {
            foreach (var line in row.WorkflowLines)
            {
                if (IsLineIncludedWhenAiFiltered(line) && CanRunAgain(row, line))
                    eligiblePairs.Add((row, line));
            }
        }

        if (eligiblePairs.Count == 0) return;

        Logger.LogInformation(
            "GHA Run again all failed requested: WorkspaceId={WorkspaceId} count={Count}",
            WorkspaceId,
            eligiblePairs.Count);

        _bulkOperationVerb = "Starting";
        _rerunTotal = eligiblePairs.Count;
        _rerunCompleted = 0;
        isRerunningAll = true;
        await InvokeAsync(StateHasChanged);

        try
        {
            using var semaphore = new SemaphoreSlim(MaxConcurrency, MaxConcurrency);
            var tasks = eligiblePairs.Select(async pair =>
            {
                await semaphore.WaitAsync(_cts.Token);
                try
                {
                    var actionEntry = BuildActionEntry(pair.Row, pair.Line);
                    Logger.LogInformation(
                        "GHA Run again all: invoking {Owner}/{Repo} branch={Branch} workflow={WorkflowName} WorkflowId={WorkflowId}",
                        actionEntry.Owner,
                        actionEntry.RepositoryName,
                        actionEntry.HeadBranch,
                        actionEntry.WorkflowName,
                        actionEntry.WorkflowId);
                    await GitHubActionsService.RunWorkflowAsync(actionEntry);
                    Logger.LogInformation(
                        "GHA Run again all: API ok {Owner}/{Repo} workflow={WorkflowName} WorkflowId={WorkflowId}",
                        actionEntry.Owner,
                        actionEntry.RepositoryName,
                        actionEntry.WorkflowName,
                        actionEntry.WorkflowId);
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex,
                        "GHA Run again all item failed: WorkspaceId={WorkspaceId} {Owner}/{Repo} WorkflowId={WorkflowId}",
                        WorkspaceId,
                        pair.Row.Repo.OrgName,
                        pair.Row.Repo.RepositoryName,
                        pair.Line.Action?.WorkflowId);
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
                "GHA Run again all finished (API phase): WorkspaceId={WorkspaceId} attempted={Count}",
                WorkspaceId,
                eligiblePairs.Count);
        }
        finally
        {
            isRerunningAll = false;
            await InvokeAsync(StateHasChanged);
            _ = RefreshAllAsync();
        }
    }
}
