using GrayMoon.Application.Features;

namespace GrayMoon.Application;

/// <summary>
/// In-process branch operations shared by REST endpoints, page handlers, and modals.
/// </summary>
public interface IWorkspaceBranchOperations
{
    Task<BranchHttpOutcome> GetBranchesAsync(int workspaceId, int repositoryId, CancellationToken cancellationToken = default);

    Task<BranchHttpOutcome> GetBranchesAsync(int workspaceId, WorkspaceFeatureContextId contextId, int repositoryId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Persisted branch state for many repositories at once (keyed by RepositoryId), with the same meaning as
    /// <see cref="GetBranchesAsync(int, WorkspaceFeatureContextId, int, CancellationToken)"/>. One database context and a
    /// fixed number of queries regardless of how many repositories; no Worker call, no remote access, no persistence and
    /// no workspace notification. Repositories not linked to the workspace are simply absent from the result.
    /// </summary>
    Task<IReadOnlyDictionary<int, WorkspaceBranchesSnapshot>> GetBranchesForRepositoriesAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        IReadOnlyCollection<int> repositoryIds,
        CancellationToken cancellationToken = default);

    Task<BranchHttpOutcome> RefreshBranchesAsync(int workspaceId, WorkspaceFeatureContextId contextId, int repositoryId, CancellationToken cancellationToken = default);

    Task<BranchHttpOutcome> CheckoutAsync(int workspaceId, WorkspaceFeatureContextId contextId, int repositoryId, string? branchName, bool isTag, CancellationToken cancellationToken = default);

    Task<BranchHttpOutcome> ReturnToDefaultAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        int repositoryId,
        string? currentBranchName,
        bool deleteRemoteBranch,
        bool allowForceDeleteLocalBranch,
        CancellationToken cancellationToken = default);

    Task<BranchHttpOutcome> GetCommonBranchesAsync(int workspaceId, CancellationToken cancellationToken = default);

    Task<BranchHttpOutcome> CountReposWithLocalBranchAsync(int workspaceId, string? branchName, CancellationToken cancellationToken = default);

    Task<BranchHttpOutcome> CreateBranchAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        int repositoryId,
        string? newBranchName,
        string? baseBranch,
        CancellationToken cancellationToken = default);

    Task<BranchHttpOutcome> SetUpstreamAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        int repositoryId,
        string? branchName,
        CancellationToken cancellationToken = default);

    Task<BranchHttpOutcome> DeleteBranchAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        int repositoryId,
        string? branchName,
        bool isRemote,
        bool force,
        CancellationToken cancellationToken = default);

    Task<BranchHttpOutcome> UpdateBranchFromDefaultAsync(int workspaceId, WorkspaceFeatureContextId contextId, int repositoryId, CancellationToken cancellationToken = default);
}
