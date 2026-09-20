using GrayMoon.Application.Features;

namespace GrayMoon.App.Services.Git;

public sealed partial class WorkspaceGitService
{
    /// <summary>
    /// Resolves agent <c>workspaceRoot</c> + folder name for the given Feature/Workspace context.
    /// Callers must pass an explicit context id - never infer from ambient UI state.
    /// </summary>
    private Task<(string WorkspaceRoot, string WorkspaceFolderName)> ResolveAgentPathArgsAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        CancellationToken cancellationToken)
        => _pathResolver.GetAgentWorkspaceArgsAsync(contextId, cancellationToken);
}
