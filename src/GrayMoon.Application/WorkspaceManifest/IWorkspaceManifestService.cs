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
    Task<WorkspaceManifestDrift> DetectDriftAsync(int workspaceId, CancellationToken cancellationToken = default);
}
