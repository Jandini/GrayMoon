namespace GrayMoon.Agent.Models;

/// <summary>
/// Live facts about one worktree, probed on the developer's machine for <c>InspectWorktree</c>.
/// Every status-dependent field is null when the worktree folder does not exist or the probe itself
/// failed, so callers never mistake "could not check" for "clean" or "zero".
/// </summary>
/// <param name="IsRegistered">True when the worktree path appears in <c>git worktree list --porcelain</c> for the main repository.</param>
/// <param name="Exists">True when the worktree folder exists on disk.</param>
/// <param name="IsLocked">True when the worktree is locked (<c>git worktree lock</c>).</param>
/// <param name="LockReason">Optional lock reason when <paramref name="IsLocked"/> is true.</param>
/// <param name="HeadSha">HEAD commit SHA, or null when it could not be determined.</param>
/// <param name="Branch">Current branch short name, or null when HEAD is detached.</param>
/// <param name="IsDirty">True when <c>git status --porcelain=v1</c> reports any change; null when the status probe failed.</param>
/// <param name="StagedCount">Count of staged (index) changes; null when the status probe failed.</param>
/// <param name="UnstagedCount">Count of unstaged (working tree) changes; null when the status probe failed.</param>
/// <param name="UntrackedCount">Count of untracked files; null when the status probe failed.</param>
/// <param name="ConflictCount">Count of unmerged (conflicted) paths; null when the status probe failed.</param>
/// <param name="HasUpstream">True when the current branch has a configured upstream.</param>
/// <param name="AheadOfUpstream">Commits on HEAD not on the upstream; null when there is no upstream.</param>
/// <param name="BehindUpstream">Commits on the upstream not on HEAD; null when there is no upstream.</param>
/// <param name="AheadOfDefault">Commits on HEAD not on <c>origin/&lt;defaultBranch&gt;</c>; null when that ref is missing.</param>
/// <param name="Error">Null on success; otherwise a short description of what could not be determined.</param>
/// <param name="FeatureBranchExists">True when the requested Feature branch exists; null when none was requested (09 SB-2, plan unit I1).</param>
/// <param name="FeatureBranchSha">HEAD commit SHA of the Feature branch; null when it does not exist or none was requested.</param>
/// <param name="FeatureBranchAheadOfDefault">Commits on the Feature branch not on <c>origin/&lt;defaultBranch&gt;</c>; null when the Feature branch or that ref is missing, or none was requested.</param>
/// <param name="FeatureBranchHasUpstream">True when the Feature branch has a configured upstream; null when it does not exist or none was requested.</param>
/// <param name="FeatureBranchAheadOfUpstream">Commits on the Feature branch not on its upstream; null when there is no upstream, the branch is missing, or none was requested.</param>
public sealed record WorktreeInspectionResult(
    bool IsRegistered,
    bool Exists,
    bool IsLocked,
    string? LockReason,
    string? HeadSha,
    string? Branch,
    bool? IsDirty,
    int? StagedCount,
    int? UnstagedCount,
    int? UntrackedCount,
    int? ConflictCount,
    bool? HasUpstream,
    int? AheadOfUpstream,
    int? BehindUpstream,
    int? AheadOfDefault,
    string? Error,
    bool? FeatureBranchExists = null,
    string? FeatureBranchSha = null,
    int? FeatureBranchAheadOfDefault = null,
    bool? FeatureBranchHasUpstream = null,
    int? FeatureBranchAheadOfUpstream = null);
