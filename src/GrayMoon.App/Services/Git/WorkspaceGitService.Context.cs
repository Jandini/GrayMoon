using GrayMoon.Application.Features;

namespace GrayMoon.App.Services.Git;

public sealed partial class WorkspaceGitService
{
    /// <summary>
    /// Resolves agent <c>workspaceRoot</c> + folder name for a Feature context
    /// (or the special Workspace context when omitted).
    /// </summary>
    private async Task<(string WorkspaceRoot, string WorkspaceFolderName)> ResolveAgentPathArgsAsync(
        int workspaceId,
        CancellationToken cancellationToken,
        WorkspaceFeatureContextId? contextId = null)
    {
        var id = contextId
            ?? await _contextResolver.GetOrCreateSpecialWorkspaceContextIdAsync(workspaceId, cancellationToken);
        return await _pathResolver.GetAgentWorkspaceArgsAsync(id, cancellationToken);
    }

    private async Task<WorkspaceFeatureContextId> ResolveContextOrSpecialAsync(
        int workspaceId,
        WorkspaceFeatureContextId? contextId,
        CancellationToken cancellationToken)
        => contextId
            ?? await _contextResolver.GetOrCreateSpecialWorkspaceContextIdAsync(workspaceId, cancellationToken);
}
