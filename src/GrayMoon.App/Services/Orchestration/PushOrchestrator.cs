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
        void OnRepoError(int id, string err)
        {
            repoErrors[id] = err;
            progress.Report($"{id}: {err}");
        }

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
                OnRepoError,
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
                OnRepoError,
                onAppSideComplete: null,
                cancellationToken: cancellationToken);
        }

        logger.LogInformation("[PushOrchestrator {RunId}] Workspace {WorkspaceId}: push finished.", runId, workspaceId);
        return OperationResult.Ok(repoErrors.Count > 0 ? new Dictionary<int, string>(repoErrors) : null);
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

