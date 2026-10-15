namespace GrayMoon.Application.Features;

public interface IWorkspaceContextPathResolver
{
    /// <summary>Resolves the physical root for a context (Workspace checkout root or Feature features directory).</summary>
    Task<string> GetContextRootAsync(WorkspaceFeatureContextId contextId, CancellationToken cancellationToken = default);

    /// <summary>Resolves the absolute repository path for a workspace-repository link inside a context.</summary>
    Task<string> GetRepositoryPathAsync(WorkspaceFeatureContextId contextId, int workspaceRepositoryId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves the absolute repository paths for several workspace-repository links inside a context in one
    /// pass. Equal to calling <see cref="GetRepositoryPathAsync"/> per id (an unknown id throws the same
    /// <see cref="InvalidOperationException"/>). The default implementation falls back to per-id calls.
    /// </summary>
    async Task<IReadOnlyDictionary<int, string>> GetRepositoryPathsAsync(
        WorkspaceFeatureContextId contextId,
        IReadOnlyCollection<int> workspaceRepositoryIds,
        CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<int, string>(workspaceRepositoryIds.Count);
        foreach (var id in workspaceRepositoryIds)
            result[id] = await GetRepositoryPathAsync(contextId, id, cancellationToken);
        return result;
    }

    /// <summary>
    /// Splits the context root into the worker <c>workspaceRoot</c> (parent) + folder name pair
    /// expected by Worker commands that call <c>GetWorkspacePath(root, name)</c>, plus the
    /// Workspace-role repository name (null when none).
    /// </summary>
    Task<WorkerWorkspaceArgs> GetWorkerArgsAsync(WorkspaceFeatureContextId contextId, CancellationToken cancellationToken = default);
}
