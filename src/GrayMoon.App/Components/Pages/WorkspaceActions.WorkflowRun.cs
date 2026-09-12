using GrayMoon.App.Models;

namespace GrayMoon.App.Components.Pages;

public sealed partial class WorkspaceActions
{
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
                    "{Operation}: attempt {Attempt}/{Max} - workflow not running in API yet WorkflowId={WorkflowId}",
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

    /// <summary>After cancel, GitHub may still report <c>in_progress</c> briefly; refresh until the workflow line is no longer running.</summary>
    private async Task<bool> TryRefreshUntilWorkflowNotRunningAsync(
        WorkspaceActionRow row,
        long workflowId,
        long runId,
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
                    "{Operation}: refresh failed for row after attempt {Attempt}; stopping cancel visibility retries",
                    operationLabel,
                    attempt);
                return false;
            }

            var wfLine = row.WorkflowLines.FirstOrDefault(l => l.Action?.RunId == runId)
                ?? (workflowId > 0
                    ? row.WorkflowLines.FirstOrDefault(l => l.Action?.WorkflowId == workflowId)
                    : null);

            if (wfLine != null && !IsLineRunningForBranch(row, wfLine))
            {
                Logger.LogInformation(
                    "{Operation}: workflow no longer running after attempt {Attempt}/{Max} WorkflowId={WorkflowId} RunId={RunId} Status={Status}",
                    operationLabel,
                    attempt,
                    RunWorkflowVisibilityMaxAttempts,
                    workflowId,
                    runId,
                    wfLine.Action?.Status);
                return true;
            }

            if (attempt < RunWorkflowVisibilityMaxAttempts)
            {
                Logger.LogDebug(
                    "{Operation}: attempt {Attempt}/{Max} - run still in progress in API WorkflowId={WorkflowId} RunId={RunId}",
                    operationLabel,
                    attempt,
                    RunWorkflowVisibilityMaxAttempts,
                    workflowId,
                    runId);
            }
        }

        Logger.LogWarning(
            "{Operation}: still running after {Max} refresh attempts WorkflowId={WorkflowId} RunId={RunId} (grid will update on next poll)",
            operationLabel,
            RunWorkflowVisibilityMaxAttempts,
            workflowId,
            runId);
        return false;
    }

    /// <summary>Latch key: GitHub workflow id when present, else run id (for lines without workflow id).</summary>
    private static long GetLineLatchKey(WorkflowActionLine line)
    {
        var w = line.Action?.WorkflowId ?? 0;
        if (w > 0) return w;
        return line.Action?.RunId ?? 0;
    }

    private static WorkflowActionButtonLatch GetOrCreateLatch(WorkspaceActionRow row, long key)
    {
        if (!row.ActionLatches.TryGetValue(key, out var latch))
        {
            latch = new WorkflowActionButtonLatch();
            row.ActionLatches[key] = latch;
        }

        return latch;
    }

    private static bool TryBeginRunLatch(WorkspaceActionRow row, long workflowId)
    {
        if (workflowId <= 0) return false;
        var latch = GetOrCreateLatch(row, workflowId);
        if (latch.RunPending || latch.AbortPending || latch.RerunPending) return false;
        latch.RunPending = true;
        return true;
    }

    private static bool TryBeginAbortLatch(WorkspaceActionRow row, WorkflowActionLine line)
    {
        var key = GetLineLatchKey(line);
        if (key <= 0) return false;
        var latch = GetOrCreateLatch(row, key);
        if (latch.RunPending || latch.AbortPending || latch.RerunPending) return false;
        latch.AbortPending = true;
        return true;
    }

    private static bool TryBeginRerunLatch(WorkspaceActionRow row, WorkflowActionLine line)
    {
        var key = GetLineLatchKey(line);
        if (key <= 0) return false;
        var latch = GetOrCreateLatch(row, key);
        if (latch.RunPending || latch.AbortPending || latch.RerunPending) return false;
        latch.RerunPending = true;
        return true;
    }

    private static void EndRunLatch(WorkspaceActionRow row, long workflowId)
    {
        if (workflowId <= 0) return;
        if (!row.ActionLatches.TryGetValue(workflowId, out var latch)) return;
        latch.RunPending = false;
        RemoveLatchIfIdle(row, workflowId, latch);
    }

    private static void EndAbortLatch(WorkspaceActionRow row, long key)
    {
        if (key <= 0) return;
        if (!row.ActionLatches.TryGetValue(key, out var latch)) return;
        latch.AbortPending = false;
        RemoveLatchIfIdle(row, key, latch);
    }

    private static void EndRerunLatch(WorkspaceActionRow row, long key)
    {
        if (key <= 0) return;
        if (!row.ActionLatches.TryGetValue(key, out var latch)) return;
        latch.RerunPending = false;
        RemoveLatchIfIdle(row, key, latch);
    }

    private static void RemoveLatchIfIdle(WorkspaceActionRow row, long key, WorkflowActionButtonLatch latch)
    {
        if (!latch.RunPending && !latch.AbortPending && !latch.RerunPending)
            row.ActionLatches.Remove(key);
    }

    private static void ApplyActionLatches(WorkspaceActionRow row)
    {
        foreach (var line in row.WorkflowLines)
        {
            var key = GetLineLatchKey(line);
            if (key <= 0 || !row.ActionLatches.TryGetValue(key, out var latch))
            {
                line.RunInProgress = false;
                line.AbortInProgress = false;
                continue;
            }

            line.RunInProgress = latch.RunPending || latch.RerunPending;
            line.AbortInProgress = latch.AbortPending;
        }
    }

    internal async Task RerunWorkflowAsync(WorkspaceActionRow row, WorkflowActionLine line)
    {
        if (line.Action?.RunId == null) return;

        var latchKey = GetLineLatchKey(line);
        if (!TryBeginRerunLatch(row, line)) return;

        row.ErrorMessage = null;
        ApplyActionLatches(row);
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
            EndRerunLatch(row, latchKey);
            ApplyActionLatches(row);
            await InvokeAsync(StateHasChanged);
        }
    }

    internal async Task AbortWorkflowAsync(WorkspaceActionRow row, WorkflowActionLine line)
    {
        if ((line.Action?.RunId ?? 0) <= 0) return;
        if (!IsLineRunningForBranch(row, line)) return;

        var workflowId = line.Action!.WorkflowId ?? 0;
        var runId = line.Action.RunId!.Value;
        var latchKey = GetLineLatchKey(line);
        if (!TryBeginAbortLatch(row, line)) return;

        row.ErrorMessage = null;
        ApplyActionLatches(row);
        await InvokeAsync(StateHasChanged);

        try
        {
            var actionEntry = BuildActionEntry(row, line);
            Logger.LogInformation(
                "GHA Cancel requested: WorkspaceId={WorkspaceId} Connector={Connector} {Owner}/{Repo} branch={Branch} workflow={WorkflowName} WorkflowId={WorkflowId} RunId={RunId}",
                WorkspaceId,
                row.Repo.ConnectorName,
                actionEntry.Owner,
                actionEntry.RepositoryName,
                actionEntry.HeadBranch,
                actionEntry.WorkflowName,
                actionEntry.WorkflowId,
                actionEntry.RunId);
            await GitHubActionsService.CancelWorkflowRunAsync(actionEntry);
            var stopped = await TryRefreshUntilWorkflowNotRunningAsync(
                row,
                workflowId,
                runId,
                "GHA Cancel",
                CancellationToken.None);
            Logger.LogInformation(
                "GHA Cancel completed (refresh phase): WorkspaceId={WorkspaceId} {Owner}/{Repo} workflow={WorkflowName} RunId={RunId} noLongerRunningVisible={Stopped}",
                WorkspaceId,
                actionEntry.Owner,
                actionEntry.RepositoryName,
                actionEntry.WorkflowName,
                runId,
                stopped);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex,
                "GHA Cancel failed: WorkspaceId={WorkspaceId} {Owner}/{Repo} workflow={WorkflowName} RunId={RunId}",
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
            EndAbortLatch(row, latchKey);
            ApplyActionLatches(row);
            await InvokeAsync(StateHasChanged);
        }
    }

    internal async Task RunWorkflowAsync(WorkspaceActionRow row, WorkflowActionLine line)
    {
        if (line.Action?.WorkflowId == null) return;
        if (IsLineRunningForBranch(row, line)) return;

        var latchKey = line.Action.WorkflowId.Value;
        if (!TryBeginRunLatch(row, latchKey)) return;

        row.ErrorMessage = null;
        ApplyActionLatches(row);
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
            EndRunLatch(row, latchKey);
            ApplyActionLatches(row);
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
}
