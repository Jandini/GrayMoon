using GrayMoon.App.Models;
using GrayMoon.App.Repositories;

namespace GrayMoon.App.Services;

/// <summary>Orchestrates GitHub Actions fetch and persistence for workspace repositories.</summary>
public sealed class WorkspaceActionService(
    WorkspaceActionRepository actionRepository,
    GitHubActionsService gitHubActionsService,
    ILogger<WorkspaceActionService> logger)
{
    /// <summary>Returns persisted action state for the workspace keyed by RepositoryId. Used when building the grid from cache.</summary>
    public async Task<IReadOnlyDictionary<int, ActionStatusInfo?>> GetPersistedActionsForWorkspaceAsync(int workspaceId, CancellationToken cancellationToken = default)
    {
        return await actionRepository.GetByWorkspaceIdAsync(workspaceId, cancellationToken);
    }

    /// <summary>
    /// Fetches the aggregate CI status for <paramref name="branch"/> of the given repository from GitHub and persists it.
    /// Returns the fetched status, or null if the repository has no valid connector/org.
    /// </summary>
    public async Task<ActionStatusInfo?> FetchAndPersistAsync(
        int workspaceRepositoryId,
        GitHubRepositoryEntry repository,
        string branch,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var info = await gitHubActionsService.GetAggregateActionStatusForBranchAsync(repository, branch);
            await actionRepository.UpsertAsync(workspaceRepositoryId, info, cancellationToken);
            return info;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "FetchAndPersist failed for {Owner}/{Repo} branch={Branch}",
                repository.OrgName, repository.RepositoryName, branch);
            return null;
        }
    }
}
