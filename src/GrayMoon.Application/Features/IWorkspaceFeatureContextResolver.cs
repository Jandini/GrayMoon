namespace GrayMoon.Application.Features;

public interface IWorkspaceFeatureContextResolver
{
    /// <summary>Returns the special Workspace context id for a workspace, creating it if missing.</summary>
    Task<WorkspaceFeatureContextId> GetOrCreateSpecialWorkspaceContextIdAsync(int workspaceId, CancellationToken cancellationToken = default);

    /// <summary>Loads context metadata. Throws if missing or workspace mismatch when workspaceId is supplied.</summary>
    Task<WorkspaceFeatureContextInfo> GetRequiredAsync(WorkspaceFeatureContextId contextId, int? expectedWorkspaceId = null, CancellationToken cancellationToken = default);

    /// <summary>Lists contexts for a workspace (Workspace first, then Features by name).</summary>
    Task<IReadOnlyList<WorkspaceFeatureContextInfo>> ListForWorkspaceAsync(int workspaceId, CancellationToken cancellationToken = default);
}

public sealed class WorkspaceFeatureContextInfo
{
    public required WorkspaceFeatureContextId ContextId { get; init; }
    public required int WorkspaceId { get; init; }
    public required bool IsSpecialWorkspace { get; init; }
    public int? WorkspaceFeatureId { get; init; }
    public string? FeatureName { get; init; }
    public string? LifecycleState { get; init; }
    public DateTime? LastSyncedAt { get; init; }
    public bool IsInSync { get; init; }
}
