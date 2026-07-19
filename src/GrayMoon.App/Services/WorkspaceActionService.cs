using System.Collections.Concurrent;
using GrayMoon.App.Models;
using GrayMoon.App.Repositories;

namespace GrayMoon.App.Services;

/// <summary>Orchestrates GitHub Actions fetch and persistence for workspace repositories.</summary>
public sealed class WorkspaceActionService(
    WorkspaceActionRepository actionRepository,
    GitHubActionsService gitHubActionsService)
{
    /// <summary>Coalesces concurrent <see cref="FetchAndPersistAsync"/> callers for the same row (multiple open tabs, grid auto-poll overlapping push discovery) onto one in-flight GitHub fetch.</summary>
    private static readonly ConcurrentDictionary<int, Task<IReadOnlyList<ActionStatusInfo>?>> InFlightFetches = new();

    /// <summary>Returns persisted action state for the workspace keyed by RepositoryId. Used when building the grid from cache.</summary>
    public async Task<IReadOnlyDictionary<int, RepositoryActionsPersistedState>> GetPersistedActionsForWorkspaceAsync(int workspaceId, CancellationToken cancellationToken = default)
    {
        return await actionRepository.GetByWorkspaceIdAsync(workspaceId, cancellationToken);
    }

    /// <summary>
    /// Fetches CI status per workflow for <paramref name="branch"/> from GitHub and persists it.
    /// Returns null if the repository has no valid connector/org (nothing persisted).
    /// Throws on GitHub API errors (e.g. HTTP 401/403) so the caller can surface them as error badges.
    /// Concurrent callers for the same <paramref name="workspaceRepositoryId"/> coalesce onto one in-flight fetch.
    /// </summary>
    public Task<IReadOnlyList<ActionStatusInfo>?> FetchAndPersistAsync(
        int workspaceRepositoryId,
        GitHubRepositoryEntry repository,
        string branch,
        CancellationToken cancellationToken = default)
    {
        if (InFlightFetches.TryGetValue(workspaceRepositoryId, out var existing) && !existing.IsCompleted)
            return existing;

        var fetchTask = FetchAndPersistCoreAsync(workspaceRepositoryId, repository, branch, cancellationToken);
        InFlightFetches[workspaceRepositoryId] = fetchTask;
        return AwaitAndClearInFlightAsync(workspaceRepositoryId, fetchTask);
    }

    private static async Task<IReadOnlyList<ActionStatusInfo>?> AwaitAndClearInFlightAsync(
        int workspaceRepositoryId,
        Task<IReadOnlyList<ActionStatusInfo>?> fetchTask)
    {
        try
        {
            return await fetchTask.ConfigureAwait(false);
        }
        finally
        {
            InFlightFetches.TryRemove(new KeyValuePair<int, Task<IReadOnlyList<ActionStatusInfo>?>>(workspaceRepositoryId, fetchTask));
        }
    }

    private async Task<IReadOnlyList<ActionStatusInfo>?> FetchAndPersistCoreAsync(
        int workspaceRepositoryId,
        GitHubRepositoryEntry repository,
        string branch,
        CancellationToken cancellationToken)
    {
        var workflows = await gitHubActionsService.GetWorkflowStatusesForBranchAsync(repository, branch, cancellationToken);
        if (workflows == null)
            return null;

        await actionRepository.UpsertAsync(workspaceRepositoryId, workflows, branch, cancellationToken);
        return workflows;
    }
}
