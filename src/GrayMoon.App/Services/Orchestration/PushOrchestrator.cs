using Microsoft.Extensions.DependencyInjection;

namespace GrayMoon.App.Services.Orchestration;

/// <summary>
/// Push workflow orchestrator: optionally sync required package registries, then push repositories either
/// dependency-synchronized (level-ordered with package wait) or non-synchronized (parallel).
/// Stateless; all UI state is owned by the caller.
/// </summary>
public sealed class PushOrchestrator(
    WorkspacePushService workspacePushService,
    IServiceProvider serviceProvider,
    ILogger<PushOrchestrator> logger)
{
    public async Task<OperationResult> RunAsync(
        int workspaceId,
        IReadOnlySet<int> repoIds,
        bool synchronizedPush,
        IReadOnlySet<string> requiredPackageIds,
        IProgress<OperationProgress>? progress = null,
        Action? onAppSideComplete = null,
        IReadOnlySet<int>? syncedRepoIds = null,
        CancellationToken cancellationToken = default,
        string? runId = null)
    {
        logger.LogInformation(
            "[PushOrchestrator {RunId}] Workspace {WorkspaceId}: starting push. Mode={Mode}, RepoCount={RepoCount}, RequiredPackages={RequiredPackages}",
            runId, workspaceId, synchronizedPush ? "synchronized" : "parallel", repoIds.Count, requiredPackageIds.Count);

        var setProgress = progress.ToMessageAction();
        var repoErrors = new System.Collections.Concurrent.ConcurrentDictionary<int, string>();
        var levelErrors = new System.Collections.Concurrent.ConcurrentDictionary<int, string>();
        var sink = new OperationErrorSink(
            workspaceId,
            logger,
            (id, err) => repoErrors[id] = err,
            (level, err) => levelErrors[level] = err);

        try
        {
            if (synchronizedPush)
            {
                setProgress("Syncing package registries for required packages...");
                if (requiredPackageIds.Count > 0 && serviceProvider.GetService<PackageRegistrySyncService>() is { } syncService)
                    await syncService.SyncRegistriesForPackageIdsAsync(workspaceId, requiredPackageIds, cancellationToken);

                setProgress("Pushing synchronized...");
                await workspacePushService.RunPushAsync(
                    workspaceId,
                    repoIds,
                    setProgress,
                    sink.Repository,
                    sink.Level,
                    onAppSideComplete,
                    packageRegistriesAlreadySynced: requiredPackageIds.Count > 0,
                    syncedRepoIds: syncedRepoIds,
                    cancellationToken: cancellationToken,
                    runId: runId);
            }
            else
            {
                setProgress("Pushing...");
                await workspacePushService.RunPushReposParallelAsync(
                    workspaceId,
                    repoIds,
                    setProgress,
                    sink.Repository,
                    sink.Level,
                    onAppSideComplete: null,
                    cancellationToken: cancellationToken);
            }
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

        logger.LogInformation("[PushOrchestrator {RunId}] Workspace {WorkspaceId}: push finished.", runId, workspaceId);
        return PushOperationResult.FromErrors(repoErrors, levelErrors);
    }

    public async Task<OperationResult> PushSingleAsync(
        int workspaceId,
        int repositoryId,
        string? branchName,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        var (success, errorMessage) = await workspacePushService.PushSingleRepositoryWithUpstreamAsync(
            workspaceId,
            repositoryId,
            branchName,
            progress.ToMessageAction(),
            cancellationToken);
        return success
            ? OperationResult.Ok()
            : OperationResult.Fail(errorMessage ?? "Push failed.");
    }
}

