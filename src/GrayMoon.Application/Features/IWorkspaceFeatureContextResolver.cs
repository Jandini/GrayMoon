namespace GrayMoon.Application.Features;

public interface IWorkspaceFeatureContextResolver
{
    /// <summary>Returns the special Workspace context id for a workspace, creating it if missing.</summary>
    Task<WorkspaceFeatureContextId> GetOrCreateSpecialWorkspaceContextIdAsync(int workspaceId, CancellationToken cancellationToken = default);

    /// <summary>Loads context metadata. Throws if missing or workspace mismatch when workspaceId is supplied.</summary>
    Task<WorkspaceFeatureContextInfo> GetRequiredAsync(WorkspaceFeatureContextId contextId, int? expectedWorkspaceId = null, CancellationToken cancellationToken = default);

    /// <summary>Lists contexts for a workspace (Workspace first, then Features by name).</summary>
    Task<IReadOnlyList<WorkspaceFeatureContextInfo>> ListForWorkspaceAsync(int workspaceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns, for each given workspace id, the count of Feature contexts (every lifecycle state).
    /// Lets callers (e.g. a "Switch Workspace" menu) show a "has Features" affordance for every
    /// workspace up front without loading each workspace's full Feature list eagerly — the detail
    /// list can then be fetched lazily, per workspace, only once the user actually asks for it.
    /// Default implementation falls back to one <see cref="ListForWorkspaceAsync"/> call per
    /// workspace id; implementations backed by a database should override this with a single
    /// grouped query.
    /// </summary>
    Task<IReadOnlyDictionary<int, int>> GetFeatureCountsAsync(IReadOnlyCollection<int> workspaceIds, CancellationToken cancellationToken = default)
    {
        return DefaultAsync();

        async Task<IReadOnlyDictionary<int, int>> DefaultAsync()
        {
            var result = new Dictionary<int, int>();
            foreach (var workspaceId in workspaceIds)
            {
                var list = await ListForWorkspaceAsync(workspaceId, cancellationToken);
                result[workspaceId] = list.Count(c => !c.IsSpecialWorkspace);
            }
            return result;
        }
    }
}

public sealed class WorkspaceFeatureContextInfo
{
    public required WorkspaceFeatureContextId ContextId { get; init; }
    public required int WorkspaceId { get; init; }
    public required bool IsSpecialWorkspace { get; init; }
    public int? WorkspaceFeatureId { get; init; }
    public string? FeatureName { get; init; }
    public string? LifecycleState { get; init; }
    /// <summary>Feature-level LastError when present (C3 tooltip).</summary>
    public string? LastError { get; init; }
    /// <summary>True when any repo row is Removing or Removed (D2 remove-incomplete; C3 label).</summary>
    public bool IsRemoveIncomplete { get; init; }
    public DateTime? LastSyncedAt { get; init; }
    public bool IsInSync { get; init; }
}
