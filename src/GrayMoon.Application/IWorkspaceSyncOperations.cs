using GrayMoon.App.Models;

namespace GrayMoon.Application;

public sealed record UnattendedReturnToDefaultResult(bool Completed, string? AbortReason);

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
        IReadOnlyList<int> repositoryIds,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken);

    /// <summary>
    /// Executes Return to Default for the given repositories with explicit options.
    /// Continues after per-repository failures and reports them via <see cref="OperationResult.RepoErrors"/>.
    /// </summary>
    Task<OperationResult> ExecuteReturnToDefaultAsync(
        int workspaceId,
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
        IReadOnlyList<int> repositoryIds,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken);

    Task<OperationResult> PullAsync(
        int workspaceId,
        int repositoryId,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken);

    Task<OperationResult> PullLevelAsync(
        int workspaceId,
        IReadOnlyList<int> repositoryIds,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken);

    Task<OperationResult> UndoPushAsync(
        int workspaceId,
        bool keepChanges,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken);

    Task<OperationResult> QuickFetchAsync(
        int workspaceId,
        IReadOnlyCollection<int>? repositoryIds,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken);
}
