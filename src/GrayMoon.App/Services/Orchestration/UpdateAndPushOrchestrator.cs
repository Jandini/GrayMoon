using System.Collections.Concurrent;
using System.Threading.Channels;
using GrayMoon.Application.Features;

namespace GrayMoon.App.Services.Orchestration;

/// <summary>
/// Update and Push as a pipeline of two lanes. The update lane walks the dependency levels (update, commit, next);
/// each finished level is handed to the push lane, which pushes it and waits for its packages while the update lane
/// carries on with the higher levels. Level 1 therefore goes out as soon as it is committed, and every level's CI and
/// package wait overlaps the update of the levels above it. Stateless; the caller owns all UI state.
/// </summary>
public sealed class UpdateAndPushOrchestrator(
    DependencyUpdateOrchestrator updateOrchestrator,
    WorkspacePushService workspacePushService,
    IServiceScopeFactory scopeFactory,
    ILogger<UpdateAndPushOrchestrator> logger)
{
    /// <summary>
    /// Runs the pipeline. Returns <see cref="UpdateAndPushResult.NotPipelined"/> without changing anything when the push
    /// lane cannot run safely (a required package has no registry mapping, or no registry is reachable): the caller then
    /// runs the sequential update-then-push, whose own checks and confirm dialog handle that case.
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
        if (!await workspacePushService.CanRunPushLaneAsync(workspaceId, contextId, maxLevel, cancellationToken))
        {
            logger.LogInformation("[UpdateAndPushOrchestrator {RunId}] Workspace {WorkspaceId}: push lane not possible, falling back to sequential.", runId, workspaceId);
            return UpdateAndPushResult.NotPipelined;
        }

        var progress = new TwoLaneProgress(reportOverlay);
        var pushRepoErrors = new ConcurrentDictionary<int, string>();
        var pushLevelErrors = new ConcurrentDictionary<int, string>();
        var channel = Channel.CreateUnbounded<DependencyLevelCompletion>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });

        // The lane has its own scope (its own DbContext): it runs concurrently with the update, which owns the caller's.
        var lane = Task.Run<int>(async () =>
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var pushService = scope.ServiceProvider.GetRequiredService<WorkspacePushService>();
            try
            {
                return await pushService.RunPushLaneAsync(
                    workspaceId,
                    contextId,
                    channel.Reader,
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
                    channel.Writer.TryWrite(completion);
                    return Task.CompletedTask;
                });
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
        var pushedCount = await lane;

        var push = PushOperationResult.FromErrors(pushRepoErrors, pushLevelErrors);
        logger.LogInformation(
            "[UpdateAndPushOrchestrator {RunId}] Workspace {WorkspaceId}: finished. UpdateSuccess={UpdateSuccess}, PushSuccess={PushSuccess}",
            runId, workspaceId, update.Success, push.Success);
        return new UpdateAndPushResult(true, update, push, pushedCount);
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
