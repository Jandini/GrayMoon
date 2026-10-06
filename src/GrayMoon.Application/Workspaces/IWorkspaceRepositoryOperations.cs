namespace GrayMoon.Application.Workspaces;

public interface IWorkspaceRepositoryOperations
{
    /// <summary>Links repositoryId as the Workspace repository and attaches it to the root (D4), then writes manifest and .gitignore (D5, D12).</summary>
    Task<OperationResult> EnableWorkspaceRepositoryAsync(int workspaceId, int repositoryId, IProgress<OperationProgress>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>Removes the Workspace-role link only. Never deletes files or .git.</summary>
    Task<OperationResult> DisableWorkspaceRepositoryAsync(int workspaceId, CancellationToken cancellationToken = default);

    Task<RestoreWorkspaceResult> RestoreFromRepositoryAsync(int repositoryId, string workspaceName, IProgress<OperationProgress>? progress = null, CancellationToken cancellationToken = default);
}

public sealed record RestoreWorkspaceResult(
    bool Success,
    int? WorkspaceId,
    string? Error,
    IReadOnlyList<string> UnresolvedConnectorUrls,
    IReadOnlyList<string> UnresolvedRepositoryUrls);
