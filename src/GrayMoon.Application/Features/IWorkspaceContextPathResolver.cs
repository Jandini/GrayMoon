namespace GrayMoon.Application.Features;

public interface IWorkspaceContextPathResolver
{
    /// <summary>Resolves the physical root for a context (Workspace checkout root or Feature features directory).</summary>
    Task<string> GetContextRootAsync(WorkspaceFeatureContextId contextId, CancellationToken cancellationToken = default);

    /// <summary>Resolves the absolute repository path for a workspace-repository link inside a context.</summary>
    Task<string> GetRepositoryPathAsync(WorkspaceFeatureContextId contextId, int workspaceRepositoryId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Splits the context root into the worker <c>workspaceRoot</c> (parent) + folder name pair
    /// expected by Worker commands that call <c>GetWorkspacePath(root, name)</c>, plus the
    /// Workspace-role repository name (null when none).
    /// </summary>
    Task<WorkerWorkspaceArgs> GetWorkerArgsAsync(WorkspaceFeatureContextId contextId, CancellationToken cancellationToken = default);
}
