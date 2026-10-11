using GrayMoon.App.Models;
using GrayMoon.Application.Features;

namespace GrayMoon.Application;

public sealed record UnattendedReturnToDefaultResult(bool Completed, string? AbortReason);

/// <summary>Why one repository in an unattended return batch did not finish. <see cref="None"/> when it completed.</summary>
public enum UnattendedReturnFailureStage
{
    None = 0,
    Fetch,
    PullRequestRefresh,
    Eligibility,
    Execution,
    Cancelled
}

/// <summary>
/// One repository's outcome from <see cref="IWorkspaceSyncOperations.ReturnToDefaultBatchAsync"/>.
/// <see cref="MutationCompleted"/> stays true when Git finished even if the later workspace refresh fails.
/// </summary>
public sealed record UnattendedReturnToDefaultRepositoryResult(
    int RepositoryId,
    bool Completed,
    string? AbortReason,
    UnattendedReturnFailureStage FailureStage,
    bool MutationAttempted,
    bool MutationCompleted);

/// <summary>
/// Per-repository unattended return results plus the single workspace reconciliation that follows the batch.
/// <see cref="ReconciliationSucceeded"/> is true when reconciliation was not needed.
/// </summary>
public sealed record UnattendedReturnToDefaultBatchResult(
    IReadOnlyList<UnattendedReturnToDefaultRepositoryResult> Results,
    bool ReconciliationAttempted,
    bool ReconciliationSucceeded,
    string? ReconciliationError)
{
    public const string CancelledMessage = "Return to default branch was cancelled.";
}

/// <summary>Per-repository facts for a Return-to-Default safety decision. Describes state and consequences, not UI.</summary>
public sealed record ReturnToDefaultRepositoryPlan(
    int RepositoryId,
    string RepositoryName,
    string? CurrentBranch,
    string? DefaultBranch,
    bool IsAlreadyOnDefault,
    bool IsOnTag,
    int CommitsAheadOfDefault,
    bool HasUpstream,
    string? PullRequestState,
    int? PullRequestNumber,
    bool CanDiscardLocalBranchSafely,
    bool RemoteBranchCanBeDeleted,
    bool RequiresExplicitDiscardConfirmation,
    string? BlockingReason);

/// <summary>Workspace-level Return-to-Default analysis produced by <see cref="IWorkspaceSyncOperations.AnalyzeReturnToDefaultAsync"/>.</summary>
public sealed record ReturnToDefaultPlan(
    int WorkspaceId,
    IReadOnlyList<ReturnToDefaultRepositoryPlan> Repositories,
    bool CanProceedAutomatically,
    bool RequiresConfirmation,
    bool HasBlockingRepositories,
    IReadOnlyList<string> BlockingReasons,
    bool PullRequestRefreshFailed,
    bool AnalysisFailed,
    string? AnalysisError)
{
    public static ReturnToDefaultPlan Failed(int workspaceId, string error) => new(
        workspaceId,
        Array.Empty<ReturnToDefaultRepositoryPlan>(),
        CanProceedAutomatically: false,
        RequiresConfirmation: false,
        HasBlockingRepositories: true,
        BlockingReasons: [error],
        PullRequestRefreshFailed: false,
        AnalysisFailed: true,
        AnalysisError: error);
}

/// <summary>Explicit destructive choices for Return-to-Default execution. Callers must not infer consent from transport.</summary>
public sealed record ReturnToDefaultOptions(
    bool DeleteRemoteBranch,
    bool AllowForceDeleteLocalBranch,
    bool CloseOpenPullRequest);

public interface IWorkspaceSyncOperations
{
    Task<IReadOnlyDictionary<int, RepoGitVersionInfo>> SyncAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        IReadOnlyList<int>? repositoryIds,
        bool skipDependencyLevelPersistence,
        CancellationToken cancellationToken,
        IProgress<OperationProgress>? progress,
        Action<int, RepoGitVersionInfo> updateRepoGitInfo,
        Action<int, RepoSyncStatus> setRepoSyncStatus);

    /// <summary>
    /// Freshness + safety analysis for Return to Default. Fetch failure yields <see cref="ReturnToDefaultPlan.AnalysisFailed"/>.
    /// PR refresh failure yields a plan with <see cref="ReturnToDefaultPlan.PullRequestRefreshFailed"/> and
    /// <see cref="ReturnToDefaultPlan.CanProceedAutomatically"/> false (does not invent merged/closed).
    /// </summary>
    Task<ReturnToDefaultPlan> AnalyzeReturnToDefaultAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        IReadOnlyList<int> repositoryIds,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken);

    /// <summary>
    /// Executes Return to Default for the given repositories with explicit options.
    /// Continues after per-repository failures and reports them via <see cref="OperationResult.RepoErrors"/>.
    /// </summary>
    Task<OperationResult> ExecuteReturnToDefaultAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        IReadOnlyList<int> repositoryIds,
        ReturnToDefaultOptions options,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken);

    /// <summary>
    /// Unattended Return to Default: analyze, require automatic eligibility, then execute with
    /// documented unattended options (delete remote when upstream exists, force-delete local, do not close PRs).
    /// Aborts the batch on the first blocking condition or execution failure.
    /// </summary>
    Task<UnattendedReturnToDefaultResult> ReturnToDefaultAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        IReadOnlyList<int> repositoryIds,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken);

    /// <summary>
    /// Unattended Return to Default for a bulk follow-up. Each distinct repository is planned and executed
    /// on its own, bounded by the workspace parallel limit. One repository's blocker or failure does not
    /// stop the others. Workspace recompute and broadcast run once after every admitted mutation has settled.
    /// </summary>
    Task<UnattendedReturnToDefaultBatchResult> ReturnToDefaultBatchAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        IReadOnlyList<int> repositoryIds,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken);

    Task<OperationResult> PullAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        int repositoryId,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken);

    Task<OperationResult> PullLevelAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        IReadOnlyList<int> repositoryIds,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken);

    Task<OperationResult> UndoPushAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        bool keepChanges,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken);

    Task<OperationResult> QuickFetchAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        IReadOnlyCollection<int>? repositoryIds,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken);
}
