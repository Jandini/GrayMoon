using System.Collections.Concurrent;
using GrayMoon.App.Models;
using GrayMoon.App.Repositories;

namespace GrayMoon.App.Services.Workspaces;

/// <summary>Orchestrates GitHub Actions fetch and persistence for workspace repositories.</summary>
public sealed class WorkspaceActionService(
    WorkspaceActionRepository actionRepository,
    GitHubActionsService gitHubActionsService)
{
    /// <summary>Coalesces concurrent <see cref="FetchAndPersistAsync"/> callers for the same row (multiple open tabs, grid auto-poll overlapping push discovery) onto one in-flight GitHub fetch.</summary>
    private static readonly ConcurrentDictionary<int, Task<IReadOnlyList<ActionStatusInfo>?>> InFlightFetches = new();

    /// <summary>Same coalescing as <see cref="InFlightFetches"/>, keyed by (ContextId, WorkspaceRepositoryId) for <see cref="FetchAndPersistContextAsync"/>.</summary>
    private static readonly ConcurrentDictionary<(int ContextId, int WorkspaceRepositoryId), Task<IReadOnlyList<ActionStatusInfo>?>> InFlightContextFetches = new();

    /// <summary>Returns persisted action state for the workspace keyed by RepositoryId. Used when building the grid from cache.</summary>
    public async Task<IReadOnlyDictionary<int, RepositoryActionsPersistedState>> GetPersistedActionsForWorkspaceAsync(int workspaceId, CancellationToken cancellationToken = default)
    {
        return await actionRepository.GetByWorkspaceIdAsync(workspaceId, cancellationToken);
    }

    /// <summary>Context-aware counterpart of <see cref="GetPersistedActionsForWorkspaceAsync"/> for a Feature context (§18/§2.2).</summary>
    public async Task<IReadOnlyDictionary<int, RepositoryActionsPersistedState>> GetPersistedActionsForWorkspaceContextAsync(int workspaceId, int contextId, CancellationToken cancellationToken = default)
    {
        return await actionRepository.GetByWorkspaceIdContextAsync(workspaceId, contextId, cancellationToken);
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

    /// <summary>
    /// Context-aware counterpart of <see cref="FetchAndPersistAsync"/>: fetches CI status per workflow for
    /// <paramref name="branch"/> (the Feature context's own checked-out branch, not the special Workspace's)
    /// and persists into <see cref="GrayMoon.App.Models.WorkspaceRepositoryContextAction"/> for that context - §18/§2.2.
    /// </summary>
    public Task<IReadOnlyList<ActionStatusInfo>?> FetchAndPersistContextAsync(
        int contextId,
        int workspaceRepositoryId,
        GitHubRepositoryEntry repository,
        string branch,
        CancellationToken cancellationToken = default)
    {
        var key = (contextId, workspaceRepositoryId);
        if (InFlightContextFetches.TryGetValue(key, out var existing) && !existing.IsCompleted)
            return existing;

        var fetchTask = FetchAndPersistContextCoreAsync(contextId, workspaceRepositoryId, repository, branch, cancellationToken);
        InFlightContextFetches[key] = fetchTask;
        return AwaitAndClearInFlightContextAsync(key, fetchTask);
    }

    private static async Task<IReadOnlyList<ActionStatusInfo>?> AwaitAndClearInFlightContextAsync(
        (int ContextId, int WorkspaceRepositoryId) key,
        Task<IReadOnlyList<ActionStatusInfo>?> fetchTask)
    {
        try
        {
            return await fetchTask.ConfigureAwait(false);
        }
        finally
        {
            InFlightContextFetches.TryRemove(new KeyValuePair<(int, int), Task<IReadOnlyList<ActionStatusInfo>?>>(key, fetchTask));
        }
    }

    private async Task<IReadOnlyList<ActionStatusInfo>?> FetchAndPersistContextCoreAsync(
        int contextId,
        int workspaceRepositoryId,
        GitHubRepositoryEntry repository,
        string branch,
        CancellationToken cancellationToken)
    {
        var workflows = await gitHubActionsService.GetWorkflowStatusesForBranchAsync(repository, branch, cancellationToken);
        if (workflows == null)
            return null;

        await actionRepository.UpsertContextAsync(contextId, workspaceRepositoryId, workflows, branch, cancellationToken);
        return workflows;
    }
}
