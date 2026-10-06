namespace GrayMoon.Application.WorkspaceManifest;

public interface IWorkspaceManifestService
{
    Task<WorkspaceManifest> BuildFromDatabaseAsync(int workspaceId, CancellationToken cancellationToken = default);
    string Serialize(WorkspaceManifest manifest);
    bool TryParse(string content, out WorkspaceManifest? manifest, out string? error);
    Task<OperationResult> WriteAuthoritativeManifestAsync(int workspaceId, CancellationToken cancellationToken = default);
    Task<OperationResult> WriteManagedGitIgnoreAsync(int workspaceId, CancellationToken cancellationToken = default);
    Task<WorkspaceManifestDrift> DetectDriftAsync(int workspaceId, CancellationToken cancellationToken = default);
}
