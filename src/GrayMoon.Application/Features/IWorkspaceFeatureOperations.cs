using GrayMoon.Application;

namespace GrayMoon.Application.Features;

public interface IWorkspaceFeatureOperations
{
    Task<CreateFeatureResult> CreateFeatureAsync(
        int workspaceId,
        string featureName,
        WorkspaceFeatureBaseKindApplication baseKind,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task<RemoveFeaturePlan> AnalyzeRemoveFeatureAsync(
        WorkspaceFeatureContextId featureContextId,
        CancellationToken cancellationToken = default);

    Task<OperationResult> RemoveFeatureAsync(
        WorkspaceFeatureContextId featureContextId,
        RemoveFeatureOptions options,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<WorkspaceFeatureContextInfo>> ListFeaturesAsync(
        int workspaceId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Per-repository parent/source branch captured at Feature creation (RepositoryId -> ParentBranchName).
    /// Empty for the special Workspace context. Values may be null when provenance was unknown.
    /// </summary>
    Task<IReadOnlyDictionary<int, string?>> GetParentBranchNamesByRepositoryIdAsync(
        WorkspaceFeatureContextId featureContextId,
        CancellationToken cancellationToken = default);
}

public enum WorkspaceFeatureBaseKindApplication
{
    CurrentWorkspace = 0
}

public sealed class CreateFeatureResult
{
    public bool Success { get; init; }
    public string? Error { get; init; }
    public string? Condition { get; init; }
    public WorkspaceFeatureContextId? ContextId { get; init; }
    public int? WorkspaceFeatureId { get; init; }
}

public sealed class RemoveFeatureOptions
{
    public bool AllowDiscardUncommitted { get; init; }
    public bool AllowForceDeleteLocalBranches { get; init; }
    public bool DeleteRemoteBranches { get; init; }
    /// <summary>True when the user authorized unlocking locked worktrees before removal (D5).</summary>
    public bool AllowUnlockWorktrees { get; init; }
}

public sealed class RemoveFeaturePlan
{
    public bool Success { get; init; }
    public string? Error { get; init; }
    public RemoveFeatureClassification Classification { get; init; }
    public IReadOnlyList<RemoveFeatureRepositoryPlan> Repositories { get; init; } = [];
    public bool IsAutomaticallySafe { get; init; }
    /// <summary>
    /// True when the live pull request refresh for this Feature's repos could not be completed
    /// (offline, rate limit). Not automatically safe in that case, but Remove is not disabled for it;
    /// only Unknown disk state (<see cref="RemoveFeatureRepositoryPlan.WorktreeStatusUnknown"/>) does that.
    /// </summary>
    public bool PullRequestStatusUnknown { get; init; }
    public string Summary { get; init; } = "";
}

public enum RemoveFeatureClassification
{
    Completed = 0,
    Abandoned = 1,
    Active = 2,
    NeedsRepair = 3
}

public sealed class RemoveFeatureRepositoryPlan
{
    public int WorkspaceRepositoryId { get; init; }
    public string RepositoryName { get; init; } = "";
    public string? WorktreePath { get; init; }
    public bool WorktreeExists { get; init; }
    /// <summary>
    /// True when the Agent could not confirm the worktree's disk state (not connected, old Worker,
    /// or InspectWorktree failed). Unknown is distinct from Missing: the folder may still exist.
    /// </summary>
    public bool WorktreeStatusUnknown { get; init; }
    public string? BranchName { get; init; }
    public string? HeadCommit { get; init; }
    public bool HasUncommittedChanges { get; init; }
    public bool HasStagedChanges { get; init; }
    public bool HasConflicts { get; init; }
    /// <summary>
    /// True when a live Feature-worktree status scan succeeded. Unknown or failed status is never
    /// treated as clean for automatic-safe classification.
    /// </summary>
    public bool LiveStatusEstablished { get; init; }
    public int? OutgoingCommits { get; init; }
    public bool HasUpstream { get; init; }
    /// <summary>Commits on HEAD not on the default branch, live from the Agent. Null means unknown, never treated as zero.</summary>
    public int? AheadOfDefault { get; init; }
    public int? PullRequestNumber { get; init; }
    public string? PullRequestState { get; init; }
    public bool? PullRequestMerged { get; init; }
    /// <summary>True when the worktree is locked (<c>git worktree lock</c>), live from the Agent. False on an older Worker that does not report this (D5).</summary>
    public bool IsLocked { get; init; }
    /// <summary>Optional lock reason when <see cref="IsLocked"/> is true.</summary>
    public string? LockReason { get; init; }
    public string? Warning { get; init; }
}
