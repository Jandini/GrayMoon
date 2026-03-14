using GrayMoon.App.Models;

namespace GrayMoon.App.Services;

/// <summary>
/// Handles push-related operations (push plan, push with dependencies, single push with upstream).
/// Stateless; all UI state is owned by the caller.
/// </summary>
public sealed class WorkspacePushHandler(
    IServiceScopeFactory serviceScopeFactory,
    ILogger<WorkspacePushHandler> logger)
{
    public async Task<(IReadOnlyList<PushRepoPayload> Payload, bool HasUnpushed)> GetPushPlanAsync(
        int workspaceId,
        IReadOnlyList<WorkspaceRepositoryLink> workspaceRepositories,
        CancellationToken cancellationToken)
    {
        await using var scope = serviceScopeFactory.CreateAsyncScope();
        var workspaceGitService = scope.ServiceProvider.GetRequiredService<WorkspaceGitService>();

        var (payload, _) = await workspaceGitService.GetPushPlanAsync(workspaceId);
        var repoIdsWithUnpushed = workspaceRepositories
            .Where(wr => (wr.OutgoingCommits ?? 0) > 0 || wr.BranchHasUpstream == false)
            .Select(wr => wr.RepositoryId)
            .ToHashSet();
        var toPush = payload.Where(p => repoIdsWithUnpushed.Contains(p.RepoId)).ToList();
        return (toPush, toPush.Count > 0);
    }

    public async Task RunPushWithDependenciesAsync(
        int workspaceId,
        IReadOnlySet<int> repoIds,
        bool synchronizedPush,
        IReadOnlySet<string> requiredPackageIds,
        Action<string> setProgress,
        Func<Task> refresh,
        Action<string> showToast,
        CancellationToken cancellationToken)
    {
        await using var scope = serviceScopeFactory.CreateAsyncScope();
        var workspaceGitService = scope.ServiceProvider.GetRequiredService<WorkspaceGitService>();

        try
        {
            if (synchronizedPush)
            {
                setProgress("Syncing package registries for required packages...");
                if (requiredPackageIds.Count > 0 && scope.ServiceProvider.GetService<PackageRegistrySyncService>() is { } syncService)
                    await syncService.SyncRegistriesForPackageIdsAsync(workspaceId, requiredPackageIds, cancellationToken);
                setProgress("Pushing synchronized...");
                await workspaceGitService.RunPushAsync(
                    workspaceId,
                    repoIds,
                    setProgress,
                    (id, err) => showToast($"{id}: {err}"),
                    packageRegistriesAlreadySynced: requiredPackageIds.Count > 0,
                    cancellationToken: cancellationToken);
            }
            else
            {
                setProgress("Pushing...");
                await workspaceGitService.RunPushReposParallelAsync(
                    workspaceId,
                    repoIds,
                    setProgress,
                    (id, err) => showToast($"{id}: {err}"),
                    cancellationToken);
            }

            await refresh();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Push with dependencies failed for workspace {WorkspaceId}", workspaceId);
            showToast(ex.Message);
            throw;
        }
    }

    public async Task<(bool Success, string? ErrorMessage)> PushSingleRepositoryWithUpstreamAsync(
        int workspaceId,
        int repositoryId,
        string? branchName,
        Action<string> setProgress,
        CancellationToken cancellationToken)
    {
        await using var scope = serviceScopeFactory.CreateAsyncScope();
        var workspaceGitService = scope.ServiceProvider.GetRequiredService<WorkspaceGitService>();
        return await workspaceGitService.PushSingleRepositoryWithUpstreamAsync(
            workspaceId,
            repositoryId,
            branchName,
            msg => setProgress(msg),
            cancellationToken);
    }
}

