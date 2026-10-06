namespace GrayMoon.Application.Workspaces;

/// <summary>
/// Resolves a workspace's derived capabilities. The one place the rest of the system asks what a
/// workspace can do, instead of inspecting persisted workspace-type enums itself.
/// </summary>
/// <remarks>
/// Resolution is keyed by <c>workspaceId</c> only, and deliberately so. A profile belongs to the
/// parent Workspace and worktree-backed Features inherit it; the only Feature-level metadata that
/// exists is worktree identity (pinned tag, parent branch, base commit, worktree path), never
/// configuration. Do not add a <c>WorkspaceFeatureContextId</c> overload or branch on context kind -
/// that would create a per-Feature profile, which this design does not have.
/// </remarks>
public interface IWorkspaceCapabilitiesResolver
{
    /// <summary>Resolves capabilities for a workspace. Throws when the workspace does not exist.</summary>
    Task<WorkspaceCapabilities> GetAsync(int workspaceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves capabilities for several workspaces in one query. For list surfaces that would
    /// otherwise issue one lookup per row.
    /// </summary>
    Task<IReadOnlyDictionary<int, WorkspaceCapabilities>> GetManyAsync(
        IReadOnlyCollection<int> workspaceIds,
        CancellationToken cancellationToken = default);
}
