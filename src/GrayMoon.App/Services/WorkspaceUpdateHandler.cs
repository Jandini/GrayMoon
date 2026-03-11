using GrayMoon.App.Models;
using Microsoft.Extensions.Logging;

namespace GrayMoon.App.Services;

/// <summary>
/// Handles dependency update operations for a workspace by delegating to WorkspaceGitService.
/// Stateless; the caller owns all UI state and file-version handling.
/// </summary>
public sealed class WorkspaceUpdateHandler(WorkspaceGitService workspaceGitService, ILogger<WorkspaceUpdateHandler> logger)
{
    /// <summary>
    /// Runs the core update flow (refresh, sync deps, optional commits) via WorkspaceGitService.RunUpdateAsync.
    /// Returns a payload to commit when running in "Update only" mode.
    /// </summary>
    public async Task<IReadOnlyList<SyncDependenciesRepoPayload>?> RunUpdateAsync(
        int workspaceId,
        bool withCommits,
        IReadOnlyList<SyncDependenciesRepoPayload>? updatePlanPayloadForUpdateOnly,
        CancellationToken cancellationToken,
        Action<string> setProgress,
        Action<int, string> setRepositoryError)
    {
        try
        {
            var syncedPayload = await workspaceGitService.RunUpdateAsync(
                workspaceId,
                withCommits,
                onProgressMessage: setProgress,
                onRepoError: (repoId, msg) => setRepositoryError(repoId, msg),
                repoIdsToUpdate: null,
                cancellationToken: cancellationToken);

            if (!withCommits)
            {
                return syncedPayload ?? updatePlanPayloadForUpdateOnly;
            }

            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Error running dependency update for workspace {WorkspaceId}", workspaceId);
            throw;
        }
    }
}

