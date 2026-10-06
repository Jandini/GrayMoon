using GrayMoon.Application.Features;

namespace GrayMoon.App.Services.Orchestration;

/// <summary>
/// Push workflow orchestrator: runs the push through the strategy the caller selected for the workspace and
/// folds repository and level failures into one result. Stateless; all UI state is owned by the caller.
/// </summary>
public sealed class PushOrchestrator(
    WorkspacePushService workspacePushService,
    ILogger<PushOrchestrator> logger)
{
    public async Task<OperationResult> RunAsync(
        IWorkspacePushStrategy strategy,
        WorkspacePushRun run,
        IProgress<OperationProgress>? progress = null,
        Action? onAppSideComplete = null,
        CancellationToken cancellationToken = default)
    {
        logger.LogInformation(
            "[PushOrchestrator {RunId}] Workspace {WorkspaceId}: starting push. Strategy={Strategy}, Mode={Mode}, RepoCount={RepoCount}, RequiredPackages={RequiredPackages}",
            run.RunId, run.WorkspaceId, strategy.GetType().Name, run.SynchronizedPush ? "synchronized" : "parallel", run.RepositoryIds.Count, run.RequiredPackageIds.Count);

        var setProgress = progress.ToMessageAction();
        var repoErrors = new System.Collections.Concurrent.ConcurrentDictionary<int, string>();
        var levelErrors = new System.Collections.Concurrent.ConcurrentDictionary<int, string>();
        var sink = new OperationErrorSink(
            run.WorkspaceId,
            logger,
            (id, err) => repoErrors[id] = err,
            (level, err) => levelErrors[level] = err);

        try
        {
            await strategy.PushAsync(run, setProgress, sink.Repository, sink.Level, onAppSideComplete, cancellationToken);
        }
        catch (SynchronizedPushNotPossibleException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            sink.Level(0, ex);
        }

        logger.LogInformation("[PushOrchestrator {RunId}] Workspace {WorkspaceId}: push finished.", run.RunId, run.WorkspaceId);
        return PushOperationResult.FromErrors(repoErrors, levelErrors);
    }

    public async Task<OperationResult> PushSingleAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        int repositoryId,
        string? branchName,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        var (success, errorMessage) = await workspacePushService.PushSingleRepositoryWithUpstreamAsync(
            workspaceId,
            contextId,
            repositoryId,
            branchName,
            progress.ToMessageAction(),
            cancellationToken);
        return success
            ? OperationResult.Ok()
            : OperationResult.Fail(errorMessage ?? "Push failed.");
    }
}
