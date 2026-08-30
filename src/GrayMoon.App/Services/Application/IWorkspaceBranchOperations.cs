namespace GrayMoon.App.Services.Application;

/// <summary>
/// In-process branch operations shared by REST endpoints, page handlers, and modals.
/// </summary>
public interface IWorkspaceBranchOperations
{
    Task<BranchHttpOutcome> GetBranchesAsync(int workspaceId, int repositoryId, CancellationToken cancellationToken = default);

    Task<BranchHttpOutcome> RefreshBranchesAsync(int workspaceId, int repositoryId, CancellationToken cancellationToken = default);

    Task<BranchHttpOutcome> CheckoutAsync(int workspaceId, int repositoryId, string? branchName, bool isTag, CancellationToken cancellationToken = default);

    Task<BranchHttpOutcome> SyncToDefaultAsync(
        int workspaceId,
        int repositoryId,
        string? currentBranchName,
        bool deleteRemoteBranch,
        bool allowForceDeleteLocalBranch,
        CancellationToken cancellationToken = default);

    Task<BranchHttpOutcome> GetCommonBranchesAsync(int workspaceId, CancellationToken cancellationToken = default);

    Task<BranchHttpOutcome> CountReposWithLocalBranchAsync(int workspaceId, string? branchName, CancellationToken cancellationToken = default);

    Task<BranchHttpOutcome> CreateBranchAsync(
        int workspaceId,
        int repositoryId,
        string? newBranchName,
        string? baseBranch,
        CancellationToken cancellationToken = default);

    Task<BranchHttpOutcome> SetUpstreamAsync(
        int workspaceId,
        int repositoryId,
        string? branchName,
        CancellationToken cancellationToken = default);

    Task<BranchHttpOutcome> DeleteBranchAsync(
        int workspaceId,
        int repositoryId,
        string? branchName,
        bool isRemote,
        bool force,
        CancellationToken cancellationToken = default);

    Task<BranchHttpOutcome> UpdateBranchFromDefaultAsync(int workspaceId, int repositoryId, CancellationToken cancellationToken = default);
}
