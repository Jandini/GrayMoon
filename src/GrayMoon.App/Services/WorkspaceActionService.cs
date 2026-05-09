using GrayMoon.App.Models;
using GrayMoon.App.Repositories;

namespace GrayMoon.App.Services;

/// <summary>Orchestrates GitHub Actions fetch and persistence for workspace repositories.</summary>
public sealed class WorkspaceActionService(
    WorkspaceActionRepository actionRepository,
    GitHubActionsService gitHubActionsService)
{
    /// <summary>Returns persisted action state for the workspace keyed by RepositoryId. Used when building the grid from cache.</summary>
    public async Task<IReadOnlyDictionary<int, RepositoryActionsPersistedState>> GetPersistedActionsForWorkspaceAsync(int workspaceId, CancellationToken cancellationToken = default)
    {
        return await actionRepository.GetByWorkspaceIdAsync(workspaceId, cancellationToken);
    }

    /// <summary>
    /// Fetches CI status per workflow for <paramref name="branch"/> from GitHub and persists it.
    /// Returns null if the repository has no valid connector/org (nothing persisted).
    /// Throws on GitHub API errors (e.g. HTTP 401/403) so the caller can surface them as error badges.
    /// </summary>
    public async Task<IReadOnlyList<ActionStatusInfo>?> FetchAndPersistAsync(
        int workspaceRepositoryId,
        GitHubRepositoryEntry repository,
        string branch,
        CancellationToken cancellationToken = default)
    {
        var workflows = await gitHubActionsService.GetWorkflowStatusesForBranchAsync(repository, branch, cancellationToken);
        if (workflows == null)
            return null;

        await actionRepository.UpsertAsync(workspaceRepositoryId, workflows, branch, cancellationToken);
        return workflows;
    }
}
