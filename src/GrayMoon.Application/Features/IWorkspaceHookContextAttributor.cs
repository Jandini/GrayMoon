namespace GrayMoon.Application.Features;

public interface IWorkspaceHookContextAttributor
{
    /// <summary>
    /// Resolves a hook/sync path to a context. Returns null when the path is unknown or ambiguous
    /// (never falls back to the special Workspace for an unmatched path).
    /// Null/empty <paramref name="repositoryPath"/> is treated as legacy special-Workspace attribution.
    /// </summary>
    Task<WorkspaceFeatureContextId?> ResolveAsync(
        int workspaceId,
        int repositoryId,
        string? repositoryPath,
        int? claimedContextId = null,
        CancellationToken cancellationToken = default);
}
