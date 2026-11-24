using GrayMoon.Application.Features;

namespace GrayMoon.Application;

public sealed record WorkspacePushPlan(
    IReadOnlySet<int> RepositoryIds,
    IReadOnlySet<string> RequiredPackageIds,
    bool HasUnpushed)
{
    /// <summary>
    /// Set when the push must not start: the published packages of a repository in scope could not be verified
    /// (registry authentication, timeout or failure). The plan is then empty and nothing was changed.
    /// </summary>
    public string? StopMessage { get; init; }

    /// <summary>
    /// Repositories that need no push although they have no remote branch yet, one sentence each: no commits, and their
    /// packages are reused or nobody needs them. Reported as "no work", separately from policy skips.
    /// </summary>
    public IReadOnlyList<string> NoWork { get; init; } = [];
}

public interface IWorkspacePushOperations
{
    /// <summary>
    /// Builds the push plan: checks archived repositories with the provider, runs the effective version start check
    /// (one GitVersion run per repository on its branch point, then the registry) and selects what to push.
    /// <paramref name="progress"/> receives each step, so a caller can show it while the plan is built.
    /// </summary>
    Task<WorkspacePushPlan> GetPlanAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        int? maxLevel = null,
        CancellationToken cancellationToken = default,
        IProgress<OperationProgress>? progress = null);

    /// <summary>Lightweight check (no dependency-package lookup) for which of the given repositories have unpushed commits or a branch never pushed upstream, scoped to <paramref name="contextId"/>.</summary>
    Task<IReadOnlySet<int>> GetRepositoryIdsNeedingPushAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        IReadOnlySet<int> repositoryIds,
        CancellationToken cancellationToken = default);

    Task<OperationResult> PushAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        IReadOnlySet<int> repositoryIds,
        bool synchronizedPush,
        IReadOnlySet<string> requiredPackageIds,
        IProgress<OperationProgress>? progress = null,
        IReadOnlySet<int>? syncedRepoIds = null,
        CancellationToken cancellationToken = default,
        string? runId = null,
        bool restorePackages = true);

    Task<OperationResult> PushPendingAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        bool synchronizedPush,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default,
        bool restorePackages = true);

    Task<OperationResult> PushSingleAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        int repositoryId,
        string? branchName,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
