using GrayMoon.App.Models;
using GrayMoon.Application.Features;
using Microsoft.Extensions.DependencyInjection;

namespace GrayMoon.App.Services.Orchestration;

/// <summary>
/// Dependency-aware push for .NET dependency workspaces: when synchronized, syncs the required package
/// registries, then pushes level by level, waiting for each level's packages and restoring before the next.
/// Otherwise pushes the selected repositories in parallel.
/// </summary>
public sealed class DotNetDependencyPushStrategy(
    WorkspacePushService workspacePushService,
    WorkspaceDependencyService dependencyService,
    IServiceProvider serviceProvider) : IWorkspacePushStrategy
{
    public async Task<IReadOnlyList<PushRepoPayload>> GetPayloadAsync(int workspaceId, WorkspaceFeatureContextId contextId, CancellationToken cancellationToken)
    {
        var (payload, _) = await workspacePushService.GetPushPlanAsync(workspaceId, contextId.Value, cancellationToken);
        return payload;
    }

    public async Task<IReadOnlySet<string>> GetRequiredPackageIdsAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        IReadOnlySet<int> repositoryIds,
        CancellationToken cancellationToken)
    {
        var depInfo = await dependencyService.GetPushDependencyInfoForRepoSetAsync(workspaceId, contextId.Value, repositoryIds, cancellationToken);
        return depInfo?.PayloadForRepo?.RequiredPackages
            .Select(r => r.PackageId?.Trim())
            .Where(id => !string.IsNullOrEmpty(id))
            .Cast<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase)
            ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    public async Task PushAsync(
        WorkspacePushRun run,
        Action<string> setProgress,
        Action<int, string> onRepoError,
        Action<int, string> onLevelError,
        Action? onAppSideComplete,
        CancellationToken cancellationToken)
    {
        if (!run.SynchronizedPush)
        {
            setProgress("Pushing...");
            await workspacePushService.RunPushReposParallelAsync(
                run.WorkspaceId,
                run.ContextId,
                run.RepositoryIds,
                setProgress,
                onRepoError,
                onLevelError,
                onAppSideComplete: null,
                cancellationToken: cancellationToken);
            return;
        }

        var registriesSynced = run.RequiredPackageIds.Count > 0 && run.Capabilities.UsesNuGetPackages;
        setProgress("Syncing package registries for required packages...");
        if (registriesSynced && serviceProvider.GetService<PackageRegistrySyncService>() is { } syncService)
            await syncService.SyncRegistriesForPackageIdsAsync(run.WorkspaceId, run.RequiredPackageIds, cancellationToken);

        setProgress("Pushing synchronized...");
        await workspacePushService.RunPushAsync(
            run.WorkspaceId,
            run.ContextId,
            run.RepositoryIds,
            setProgress,
            onRepoError,
            onLevelError,
            onAppSideComplete,
            packageRegistriesAlreadySynced: registriesSynced || !run.Capabilities.UsesNuGetPackages,
            syncedRepoIds: run.SyncedRepoIds,
            cancellationToken: cancellationToken,
            runId: run.RunId,
            restorePackages: run.RestorePackages && run.Capabilities.UsesPackageRestore);
    }
}
