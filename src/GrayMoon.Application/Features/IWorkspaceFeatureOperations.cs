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
        CancellationToken cancellationToken = default,
        IProgress<OperationProgress>? progress = null);

    Task<OperationResult> RemoveFeatureAsync(
        WorkspaceFeatureContextId featureContextId,
        RemoveFeatureOptions options,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Looks up, through the Worker, which local processes keep each of <paramref name="targets"/> in use ("Refresh blockers"
    /// in the Remove Feature dialog). Read-only. A folder that no longer exists comes back with no processes. Never throws for
    /// a Worker problem; the entries then carry <see cref="RemoveFeatureRepositoryBlockers.MayBeIncomplete"/> and a diagnostic.
    /// </summary>
    Task<IReadOnlyList<RemoveFeatureRepositoryBlockers>> InspectRemoveFeatureBlockersAsync(
        IReadOnlyList<RemoveFeatureRepositoryBlockers> targets,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Looks up which local processes are using the worktree folders of a Feature that has not been removed yet (the check the
    /// Remove Feature dialog runs before removal). Folders come from the database, never from the caller. Read-only. Never
    /// throws for a Worker problem or a lookup that took too long; the entries then carry
    /// <see cref="RemoveFeatureRepositoryBlockers.LookupFailed"/>.
    /// </summary>
    Task<IReadOnlyList<RemoveFeatureRepositoryBlockers>> InspectFeatureBlockersAsync(
        WorkspaceFeatureContextId featureContextId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Ends the processes the user selected in the Remove Feature dialog ("Kill and remove") for a Feature that still exists.
    /// The folders are resolved from the database; the Worker re-checks every selected process (still holding a folder, same
    /// start time, not protected) before ending it, and returns what still blocks each folder afterwards.
    /// </summary>
    Task<RemoveFeatureTerminateResult> TerminateFeatureBlockersAsync(
        WorkspaceFeatureContextId featureContextId,
        IReadOnlyList<RemoveFeatureProcessSelection> selections,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Same as <see cref="TerminateFeatureBlockersAsync"/> for an already removed Feature's leftover folders ("Kill and
    /// retry" in the removal report): the folders are the report's <see cref="RemoveFeatureRepositoryReport.ResidueTarget"/>
    /// paths, which the App built from its own database at remove time.
    /// </summary>
    Task<RemoveFeatureTerminateResult> TerminateLeftoverBlockersAsync(
        IReadOnlyList<RemoveFeatureRepositoryReport> report,
        IReadOnlyList<RemoveFeatureProcessSelection> selections,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// After a Remove Feature that left files behind, asks the Worker again to delete the leftovers of every report entry
    /// that has a <see cref="RemoveFeatureRepositoryReport.ResidueTarget"/> (the same guarded cleanup Remove uses), and
    /// returns the report with each such entry's residue and blocker fields refreshed. Entries without a target are
    /// returned unchanged.
    /// </summary>
    Task<IReadOnlyList<RemoveFeatureRepositoryReport>> RetryRemoveFeatureResidueAsync(
        IReadOnlyList<RemoveFeatureRepositoryReport> report,
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

    Task<bool> IsRemoveIncompleteAsync(
        WorkspaceFeatureContextId featureContextId,
        CancellationToken cancellationToken = default);

    Task<FeatureStatusSnapshot?> GetFeatureStatusAsync(
        WorkspaceFeatureContextId featureContextId,
        CancellationToken cancellationToken = default);

    Task<RepairFeatureResult> RepairFeatureAsync(
        WorkspaceFeatureContextId featureContextId,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task<RollbackFeatureResult> RollbackFeatureAsync(
        WorkspaceFeatureContextId featureContextId,
        IProgress<OperationProgress>? progress = null,
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
    /// <summary>
    /// When <see cref="Condition"/> is <c>BranchExists</c>, the repositories (and refs) that already
    /// have the requested Feature name. Null for every other condition.
    /// </summary>
    public IReadOnlyList<CreateFeatureBranchCollision>? BranchCollisions { get; init; }
    /// <summary>Total Workspace repositories considered when checking collisions; null when not a BranchExists failure.</summary>
    public int? TotalRepositoryCount { get; init; }
}

/// <summary>One repository where the requested Feature branch name already exists (E5).</summary>
public sealed class CreateFeatureBranchCollision
{
    public string RepositoryName { get; init; } = "";
    public IReadOnlyList<string> Refs { get; init; } = [];
}

public sealed class RemoveFeatureOptions
{
    public bool AllowDiscardUncommitted { get; init; }
    public bool AllowForceDeleteLocalBranches { get; init; }
    /// <summary>When true (default), delete local Feature branches after removing worktrees (D4).</summary>
    public bool DeleteLocalBranches { get; init; } = true;
    /// <summary>When true, delete remote Feature branches with a lease (D4). Default false; the dialog sets this when remotes exist.</summary>
    public bool DeleteRemoteBranches { get; init; }
    /// <summary>True when the user authorized unlocking locked worktrees before removal (D5).</summary>
    public bool AllowUnlockWorktrees { get; init; }
    /// <summary>
    /// Plan already shown in the Remove dialog. When set and successful, Remove skips a second
    /// analyze so the structural overlay can start immediately after the user clicks Remove.
    /// </summary>
    public RemoveFeaturePlan? AnalyzedPlan { get; init; }
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
    /// True when the Worker could not confirm the worktree's disk state (not connected, old Worker,
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
    /// <summary>Commits on HEAD not on the default branch, live from the Worker. Null means unknown, never treated as zero.</summary>
    public int? AheadOfDefault { get; init; }
    public int? PullRequestNumber { get; init; }
    public string? PullRequestState { get; init; }
    public bool? PullRequestMerged { get; init; }
    /// <summary>True when the worktree is locked (<c>git worktree lock</c>), live from the Worker. False on an older Worker that does not report this (D5).</summary>
    public bool IsLocked { get; init; }
    /// <summary>Optional lock reason when <see cref="IsLocked"/> is true.</summary>
    public string? LockReason { get; init; }
    public string? Warning { get; init; }

    /// <summary>
    /// The branch Remove actually deletes for this repository: the Feature's own name, or null for a
    /// tag-pinned repository (no branch is ever deleted for it). Set regardless of what the worktree
    /// is currently checked out to (09 SB-2).
    /// </summary>
    public string? FeatureBranchName { get; init; }

    /// <summary>Live current branch short name from the Worker; null when detached or unknown.</summary>
    public string? CheckedOutBranch { get; init; }

    /// <summary>
    /// True for a non-pinned repository whose <see cref="CheckedOutBranch"/> differs from
    /// <see cref="FeatureBranchName"/> (ordinal comparison; a null <see cref="CheckedOutBranch"/>
    /// counts as different). Always false for a tag-pinned repository (09 SB-2).
    /// </summary>
    public bool IsOffFeatureBranch { get; init; }

    /// <summary>True when <see cref="FeatureBranchName"/> exists as a ref, live from the Worker. Null when there is no Feature branch to check, or unknown (older Worker).</summary>
    public bool? FeatureBranchExists { get; init; }

    /// <summary>Commits on the Feature branch not on the default branch, live from the Worker. Null means unknown, never treated as zero (09 SB-2).</summary>
    public int? FeatureBranchAheadOfDefault { get; init; }

    /// <summary>True when the Feature branch has a configured upstream, live from the Worker. Null when unknown.</summary>
    public bool? FeatureBranchHasUpstream { get; init; }

    /// <summary>Commits on the Feature branch not on its upstream, live from the Worker. Null when unknown.</summary>
    public int? FeatureBranchAheadOfUpstream { get; init; }

    /// <summary>SHA of the Feature branch tip, live from the Worker. Used for lease-based remote delete (D4). Null when unknown or missing.</summary>
    public string? FeatureBranchSha { get; init; }

    /// <summary>
    /// Commits ahead of the default branch for whichever branch Remove will actually delete: the
    /// Feature branch for a non-pinned repository (even when the worktree has drifted to another
    /// branch), or this repository's own <see cref="AheadOfDefault"/> when there is no Feature branch
    /// to judge (a tag-pinned repository, or a plan built without setting <see cref="FeatureBranchName"/>).
    /// Null means unknown, never zero (09 SB-2).
    /// </summary>
    public int? EffectiveAheadOfDefault => FeatureBranchName is null ? AheadOfDefault : FeatureBranchAheadOfDefault;

    /// <summary>
    /// Commits not on its upstream for whichever branch Remove will actually delete, with the same
    /// fallback rule as <see cref="EffectiveAheadOfDefault"/> (09 SB-2).
    /// </summary>
    public int? EffectiveOutgoingCommits => FeatureBranchName is null ? OutgoingCommits : FeatureBranchAheadOfUpstream;
}
