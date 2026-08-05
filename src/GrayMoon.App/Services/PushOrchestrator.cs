using Microsoft.Extensions.DependencyInjection;

namespace GrayMoon.App.Services;

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
    public async Task RunAsync(
        int workspaceId,
        IReadOnlySet<int> repoIds,
        bool synchronizedPush,
        IReadOnlySet<string> requiredPackageIds,
        Action<string> setProgress,
        Action<string> showToast,
        Action? onAppSideComplete = null,
        IReadOnlySet<int>? syncedRepoIds = null,
        CancellationToken cancellationToken = default,
        string? runId = null)
    {
        logger.LogInformation(
            "[PushOrchestrator {RunId}] Workspace {WorkspaceId}: starting push. Mode={Mode}, RepoCount={RepoCount}, RequiredPackages={RequiredPackages}",
            runId, workspaceId, synchronizedPush ? "synchronized" : "parallel", repoIds.Count, requiredPackageIds.Count);

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
                (id, err) => showToast($"{id}: {err}"),
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
                (id, err) => showToast($"{id}: {err}"),
                onAppSideComplete: null,
                cancellationToken: cancellationToken);
        }

        logger.LogInformation("[PushOrchestrator {RunId}] Workspace {WorkspaceId}: push finished.", runId, workspaceId);
    }

    public Task<(bool Success, string? ErrorMessage)> PushSingleAsync(
        int workspaceId,
        int repositoryId,
        string? branchName,
        Action<string> setProgress,
        CancellationToken cancellationToken)
    {
        return workspacePushService.PushSingleRepositoryWithUpstreamAsync(
            workspaceId,
            repositoryId,
            branchName,
            msg => setProgress(msg),
            cancellationToken);
    }
}

