namespace GrayMoon.Application.Features;

public interface IWorkspaceContextPathResolver
{
    /// <summary>Resolves the physical root for a context (Workspace checkout root or Feature features directory).</summary>
    Task<string> GetContextRootAsync(WorkspaceFeatureContextId contextId, CancellationToken cancellationToken = default);

    /// <summary>Resolves the absolute repository path for a workspace-repository link inside a context.</summary>
    Task<string> GetRepositoryPathAsync(WorkspaceFeatureContextId contextId, int workspaceRepositoryId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Splits the context root into the agent <c>workspaceRoot</c> (parent) + folder name pair
    /// expected by Agent commands that call <c>GetWorkspacePath(root, name)</c>.
    /// </summary>
    Task<(string AgentWorkspaceRoot, string AgentWorkspaceFolderName)> GetAgentWorkspaceArgsAsync(
        WorkspaceFeatureContextId contextId,
        CancellationToken cancellationToken = default);
}
