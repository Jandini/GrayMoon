namespace GrayMoon.Application.WorkspaceManifest;

public interface IWorkspaceManifestService
{
    Task<WorkspaceManifest> BuildFromDatabaseAsync(int workspaceId, CancellationToken cancellationToken = default);
    string Serialize(WorkspaceManifest manifest);
    bool TryParse(string content, out WorkspaceManifest? manifest, out string? error);
    Task<OperationResult> WriteAuthoritativeManifestAsync(int workspaceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates tag pins on existing repository entries in <c>.graymoon.json</c> and writes the file when it changed.
    /// Does not create a git commit. Does nothing when the Workspace has no Workspace repository.
    /// </summary>
    Task<OperationResult> SetRepositoryTagPinsAsync(int workspaceId, IReadOnlyList<WorkspaceRepositoryTagPinChange> changes, CancellationToken cancellationToken = default);
    Task<OperationResult> WriteManagedGitIgnoreAsync(int workspaceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// True when the Workspace definition (<c>.graymoon.json</c> of the special Workspace) has <c>"codegraph": true</c>.
    /// False when there is no Workspace repository, no definition, or it cannot be read. Never throws.
    /// </summary>
    Task<bool> IsCodeGraphEnabledAsync(int workspaceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes <c>codegraph.json</c> (its <c>include</c> lists every source repository folder) into the Workspace
    /// repository of <paramref name="contextId"/>, or of the special Workspace when null. Does not check the
    /// <c>codegraph</c> setting; does nothing when the Workspace has no Workspace repository.
    /// </summary>
    Task<OperationResult> WriteCodeGraphConfigAsync(int workspaceId, Features.WorkspaceFeatureContextId? contextId = null, CancellationToken cancellationToken = default);
    Task<WorkspaceManifestDrift> DetectDriftAsync(int workspaceId, CancellationToken cancellationToken = default);
}
