using Microsoft.Extensions.DependencyInjection;

namespace GrayMoon.App.Services;

/// <summary>
/// Push workflow orchestrator: optionally sync required package registries, then push repositories either
/// dependency-synchronized (level-ordered with package wait) or non-synchronized (parallel).
/// Stateless; all UI state is owned by the caller.
/// </summary>
public sealed class PushOrchestrator(
    WorkspacePushService workspacePushService,
    IServiceProvider serviceProvider)
{
    public async Task RunAsync(
        int workspaceId,
        IReadOnlySet<int> repoIds,
        bool synchronizedPush,
        IReadOnlySet<string> requiredPackageIds,
        Action<string> setProgress,
        Func<Task> refresh,
        Action<string> showToast,
        Action? onAppSideComplete = null,
        CancellationToken cancellationToken = default)
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
                (id, err) => showToast($"{id}: {err}"),
                onAppSideComplete,
                packageRegistriesAlreadySynced: requiredPackageIds.Count > 0,
                cancellationToken: cancellationToken);
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

        await refresh();
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

