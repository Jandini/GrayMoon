using System.Collections.Concurrent;
using System.Threading.Channels;
using GrayMoon.Application.Features;

namespace GrayMoon.App.Services.Orchestration;

/// <summary>
/// Update and Push as a pipeline of two lanes. The update lane walks the dependency levels (update, commit, next);
/// each finished level is handed to the push lane, which pushes it and waits for its packages while the update lane
/// carries on with the higher levels. Level 1 therefore goes out as soon as it is committed, and every level's CI and
/// package wait overlaps the update of the levels above it. Stateless; the caller owns all UI state.
/// <para>
/// When the push lane stops (a failed level, a package-wait timeout or an exception), the update lane stops at its next
/// level boundary instead of committing levels nothing will push. The push result then names the failed level, the
/// levels that are committed locally but not pushed, and where the update stopped.
/// </para>
/// </summary>
public sealed class UpdateAndPushOrchestrator(
    IDependencyUpdateLane updateOrchestrator,
    IPushLane workspacePushService,
    IServiceScopeFactory scopeFactory,
    ILogger<UpdateAndPushOrchestrator> logger)
{
    /// <summary>
    /// Runs the pipeline. Returns <see cref="UpdateAndPushResult.NotPipelined"/> without changing anything when the push
    /// lane cannot run safely (a required package has no registry and this run will not publish it, or no registry is
    /// reachable). A package created by a repository this run pushes is matched after that level is pushed. The caller
    /// then runs the sequential update-then-push, whose own checks and confirm dialog handle the refused case.
    /// </summary>
    /// <param name="reportOverlay">Receives the combined two-lane overlay message every time either lane reports.</param>
    public async Task<UpdateAndPushResult> RunAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        CancellationToken cancellationToken,
        Action<string> reportOverlay,
        Action<int, string> setRepositoryError,
        Action<int, string> setLevelError,
        string? commitMessage = null,
        bool includeDepsInCommitMessage = true,
        int? maxLevel = null,
        bool restorePackages = true,
        string? runId = null)
    {
        runId = GrayMoon.Abstractions.Logging.GrayMoonOperationContext.EnsureId(runId);
        if (!await workspacePushService.CanRunPushLaneAsync(workspaceId, contextId, maxLevel, cancellationToken))
        {
            logger.LogInformation("[UpdateAndPushOrchestrator {RunId}] Workspace {WorkspaceId}: push lane not possible, falling back to sequential.", runId, workspaceId);
            return UpdateAndPushResult.NotPipelined;
        }

        var progress = new TwoLaneProgress(reportOverlay);
        var pushRepoErrors = new ConcurrentDictionary<int, string>();
        var pushLevelErrors = new ConcurrentDictionary<int, string>();
        var channel = Channel.CreateUnbounded<DependencyLevelCompletion>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        var laneReader = new TrackingLevelReader(channel.Reader);
        var handedLevels = new List<int>();
        int? updateStoppedAfterLevel = null;
        var laneEnded = false;

        // The lane has its own scope (its own DbContext): it runs concurrently with the update, which owns the caller's.
        // RunPushLaneAsync also gives every level a scope of its own, since the update rewrites rows between levels.
        var lane = Task.Run<int>(async () =>
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var pushService = scope.ServiceProvider.GetRequiredService<IPushLane>();
            try
            {
                return await pushService.RunPushLaneAsync(
                    workspaceId,
                    contextId,
                    laneReader,
                    progress.Push,
                    (repoId, message) =>
                    {
                        pushRepoErrors[repoId] = message;
                        setRepositoryError(repoId, message);
                    },
                    (level, message) =>
                    {
                        pushLevelErrors[level] = message;
                        setLevelError(level, message);
                    },
                    restorePackages,
                    runId,
                    cancellationToken);
            }
            finally
            {
                // Set before anything else: the task itself only completes after the scope is disposed, and a level
                // that finishes in that gap must already see the lane as stopped.
                Volatile.Write(ref laneEnded, true);
                progress.EndPush();
            }
        }, CancellationToken.None);

        DependencyUpdateRunResult update;
        try
        {
            update = await updateOrchestrator.RunAsync(
                workspaceId,
                contextId,
                cancellationToken,
                new SynchronousProgress(progress.Update),
                setRepositoryError,
                setLevelError,
                onAppSideComplete: null,
                repoIdsToUpdate: null,
                commitMessage,
                includeDepsInCommitMessage,
                maxLevel,
                runId,
                onLevelCompleted: completion =>
                {
                    // The lane only ends before the channel is completed when it stopped: a level failed (reported
                    // through the level error first) or it threw. Committing further levels would leave commits
                    // nothing pushes, so the update stops here, at the boundary it has just reached.
                    if (Volatile.Read(ref laneEnded) || lane.IsCompleted || !pushLevelErrors.IsEmpty)
                    {
                        updateStoppedAfterLevel = completion.Level;
                        throw new PushLaneStoppedException();
                    }

                    handedLevels.Add(completion.Level);
                    channel.Writer.TryWrite(completion);
                    return Task.CompletedTask;
                });
        }
        catch (PushLaneStoppedException)
        {
            // Stopped on purpose. The push result below carries the whole account, so the update is not a failure.
            update = DependencyUpdateRunResult.Ok();
            logger.LogWarning(
                "[UpdateAndPushOrchestrator {RunId}] Workspace {WorkspaceId}: push lane stopped; update stopped after level {Level}.",
                runId, workspaceId, updateStoppedAfterLevel);
        }
        catch
        {
            // Stop feeding the lane, then let it finish what it already has before the failure surfaces.
            channel.Writer.TryComplete();
            progress.EndUpdate();
            await SettleLaneAsync(lane, runId);
            throw;
        }

        channel.Writer.TryComplete();
        progress.EndUpdate();
        var pushedCount = 0;
        Exception? laneFault = null;
        try
        {
            pushedCount = await lane;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A lane that threw is a failed push with a level and a reason, not an exception that surfaces only
            // after every remaining level was committed.
            laneFault = ex;
            logger.LogError(ex, "[UpdateAndPushOrchestrator {RunId}] Workspace {WorkspaceId}: push lane failed.", runId, workspaceId);
        }

        var push = BuildPushResult(
            pushRepoErrors, pushLevelErrors, laneFault, laneReader.LastTakenLevel, laneReader.TakenCount, handedLevels, updateStoppedAfterLevel, setLevelError);
        var failedPushLevel = pushLevelErrors.Count == 0 ? "none" : string.Join(",", pushLevelErrors.Keys.OrderBy(level => level));
        logger.LogInformation(
            "[UpdateAndPushOrchestrator {RunId}] Workspace {WorkspaceId}: finished. UpdateSuccess={UpdateSuccess}, PushSuccess={PushSuccess}, PushedRepoCount={PushedRepoCount}, FailedPushLevel={FailedPushLevel}",
            runId, workspaceId, update.Success, push.Success, pushedCount, failedPushLevel);
        return new UpdateAndPushResult(true, update, push, pushedCount);
    }

    /// <summary>
    /// Turns what the push lane reported into the run's push result. A stopped lane gets one message on its failed
    /// level: the reason, the levels committed locally but not pushed, and where the update stopped.
    /// </summary>
    internal static OperationResult BuildPushResult(
        IReadOnlyDictionary<int, string> repoErrors,
        IReadOnlyDictionary<int, string> levelErrors,
        Exception? laneFault,
        int? lastTakenLevel,
        int takenCount,
        IReadOnlyList<int> handedLevels,
        int? updateStoppedAfterLevel,
        Action<int, string> setLevelError)
    {
        if (repoErrors.Count == 0 && levelErrors.Count == 0 && laneFault == null)
            return OperationResult.Ok();

        // Restore warnings arrive as repository errors but do not stop the lane: every level was still pushed.
        var laneFinished = laneFault == null && levelErrors.Count == 0 && updateStoppedAfterLevel == null && takenCount == handedLevels.Count;
        // That is a finished push with warnings, not a failed run.
        if (laneFinished && PushOperationResult.TryDoneWithRestoreWarnings(repoErrors) is { } warned)
            return warned;

        var failedLevel = levelErrors.Count > 0 ? levelErrors.Keys.Min() : lastTakenLevel ?? 0;
        var notPushed = handedLevels.Where(level => level > failedLevel).ToList();
        if (updateStoppedAfterLevel is int boundary && boundary > failedLevel)
            notPushed.Add(boundary);

        var aftermath = new List<string>();
        if (notPushed.Count > 0)
        {
            aftermath.Add(notPushed.Count == 1
                ? $"Level {notPushed[0]} is committed locally but not pushed."
                : $"Levels {string.Join(", ", notPushed)} are committed locally but not pushed.");
        }
        if (updateStoppedAfterLevel is int stoppedAfter)
            aftermath.Add($"The update stopped after level {stoppedAfter}.");
        var aftermathText = string.Join(" ", aftermath);

        var levels = new Dictionary<int, string>(levelErrors);
        var hadLevelError = levels.TryGetValue(failedLevel, out var reported);
        reported ??= laneFault?.Message ?? PushOperationResult.StoppedAfterFailures;
        var parts = reported.Split('\n', 2);
        var reason = parts[0].Trim();

        if (aftermathText.Length > 0 || !hadLevelError)
        {
            // The aftermath goes on the first line: that is the line the closing message and the level callout show.
            var firstLine = aftermathText.Length > 0 ? $"{reason} {aftermathText}" : reason;
            levels[failedLevel] = parts.Length > 1 ? $"{firstLine}\n{parts[1]}" : firstLine;
            setLevelError(failedLevel, levels[failedLevel]);
        }

        var summary = failedLevel > 0 ? $"Push stopped at level {failedLevel}: {reason}" : $"Push stopped: {reason}";
        return OperationResult.Fail(
            aftermathText.Length > 0 ? $"{summary} {aftermathText}" : summary,
            repoErrors.Count > 0 ? new Dictionary<int, string>(repoErrors) : null,
            levels);
    }

    /// <summary>Thrown from the level hand-off to stop the update lane at a level boundary. Never leaves this class.</summary>
    private sealed class PushLaneStoppedException : Exception;

    /// <summary>The lane's view of the level channel. Records the last level the lane took, which is the level it stopped on.</summary>
    private sealed class TrackingLevelReader(ChannelReader<DependencyLevelCompletion> inner) : ChannelReader<DependencyLevelCompletion>
    {
        private int _lastTakenLevel = -1;
        private int _takenCount;

        public int? LastTakenLevel => Volatile.Read(ref _lastTakenLevel) is var level and >= 0 ? level : null;

        public int TakenCount => Volatile.Read(ref _takenCount);

        public override Task Completion => inner.Completion;

        public override bool TryRead(out DependencyLevelCompletion item)
        {
            if (!inner.TryRead(out item!))
                return false;
            Volatile.Write(ref _lastTakenLevel, item.Level);
            Interlocked.Increment(ref _takenCount);
            return true;
        }

        public override ValueTask<bool> WaitToReadAsync(CancellationToken cancellationToken = default)
            => inner.WaitToReadAsync(cancellationToken);
    }

    private async Task SettleLaneAsync(Task lane, string? runId)
    {
        try
        {
            await lane;
        }
        catch (Exception ex)
        {
            // The update's own exception is the one being rethrown; the lane's is only worth a log line.
            logger.LogWarning(ex, "[UpdateAndPushOrchestrator {RunId}] Push lane ended with an error while the update was failing.", runId);
        }
    }
}
