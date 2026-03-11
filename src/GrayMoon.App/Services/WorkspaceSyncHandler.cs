using GrayMoon.App.Models;

namespace GrayMoon.App.Services;

/// <summary>
/// Handles sync operations (git status, version, branch, commit counts) for workspace repositories.
/// Stateless; all state is provided via callbacks.
/// </summary>
public sealed class WorkspaceSyncHandler(ILogger<WorkspaceSyncHandler> logger, IServiceScopeFactory serviceScopeFactory)
{
    public async Task<IReadOnlyDictionary<int, RepoGitVersionInfo>> RunSyncAsync(
        int workspaceId,
        IReadOnlyList<int>? repositoryIds,
        bool skipDependencyLevelPersistence,
        CancellationToken cancellationToken,
        Action<string> setProgress,
        Action<int, RepoGitVersionInfo> updateRepoGitInfo,
        Action<int, RepoSyncStatus> setRepoSyncStatus)
    {
        await using var scope = serviceScopeFactory.CreateAsyncScope();
        var workspaceGitService = scope.ServiceProvider.GetRequiredService<WorkspaceGitService>();

        try
        {
            var results = await workspaceGitService.SyncAsync(
                workspaceId,
                onProgress: (completed, total, repoId, info) =>
                {
                    setProgress($"Synchronized {completed} of {total}");
                    var status = !string.IsNullOrWhiteSpace(info.ErrorMessage) || info.Version != "-" || info.Branch != "-"
                        ? RepoSyncStatus.InSync
                        : RepoSyncStatus.Error;
                    setRepoSyncStatus(repoId, status);
                    updateRepoGitInfo(repoId, info);
                },
                repositoryIds: repositoryIds,
                skipDependencyLevelPersistence: skipDependencyLevelPersistence,
                cancellationToken: cancellationToken);

            return results;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Error running workspace sync for WorkspaceId={WorkspaceId}", workspaceId);
            throw;
        }
    }
}

