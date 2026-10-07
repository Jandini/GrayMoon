using GrayMoon.Common.Git;
using GrayMoon.Worker.Models;
using GrayMoon.Worker.Services;

namespace GrayMoon.Worker.Abstractions;

/// <summary>Feature worktree lifecycle and inspection. Native git remains authoritative for add/remove.</summary>
public interface IGitWorktreeService
{
    /// <summary>Lists worktrees for the repository at <paramref name="mainRepositoryPath"/> via <c>git worktree list --porcelain</c>.</summary>
    Task<(bool Success, IReadOnlyList<GitWorktreeInfo> Worktrees, string? ErrorCode, string? ErrorMessage)> ListWorktreesAsync(
        string mainRepositoryPath,
        CancellationToken ct);
    /// <summary>
    /// Creates a linked worktree with a new branch from <paramref name="baseCommitSha"/> (offline-safe), or a
    /// detached worktree at that commit when <paramref name="branchName"/> is null.
    /// Never passes <c>--force</c>. Idempotent when the expected path already has the expected branch
    /// (or, when detached, is already detached at <paramref name="baseCommitSha"/>).
    /// </summary>
    Task<(bool Success, GitWorktreeInfo? Worktree, bool AlreadyExisted, string? ErrorCode, string? ErrorMessage)> CreateWorktreeAsync(
        string mainRepositoryPath,
        string worktreePath,
        string? branchName,
        string baseCommitSha,
        CancellationToken ct);

    /// <summary>
    /// Removes a linked worktree. When <paramref name="force"/> is false uses a clean remove;
    /// force is only for callers that have already authorized discard of dirty state.
    /// After the Git-level remove (or when the path was already unregistered), if the worktree
    /// folder still has files, deletes them with a custom walk (retries, reparse-point-safe) only
    /// when every safety guard passes for <paramref name="featureRootPath"/> and
    /// <paramref name="featureStorageRoot"/>; otherwise the leftover is only reported.
    /// When <paramref name="featureRootPath"/> becomes empty afterward, it is removed too.
    /// Without <paramref name="featureRootPath"/> and <paramref name="featureStorageRoot"/>, no
    /// residue is deleted, matching today's behaviour for an old caller.
    /// When <paramref name="unlock"/> is true, runs <c>git worktree unlock</c> before the remove, so a
    /// locked worktree (<c>git worktree lock</c>) can be removed (D5). Defaults to false, matching
    /// today's behaviour for an old caller.
    /// </summary>
    Task<(bool Success, bool AlreadyRemoved, string? ErrorCode, string? ErrorMessage, WorktreeResidueResult Residue)> RemoveWorktreeAsync(
        string mainRepositoryPath,
        string worktreePath,
        bool force,
        CancellationToken ct,
        string? featureRootPath = null,
        string? featureStorageRoot = null,
        bool unlock = false);

    /// <summary>
    /// Reports everything removal needs to know about one worktree, checked live: registration,
    /// existence, lock state, dirty state, and commit counts vs upstream and the default branch.
    /// Returns facts only (no exception) even when the worktree folder does not exist. When
    /// <paramref name="featureBranch"/> is set, the result also reports that branch's own facts
    /// (<c>refs/heads/&lt;featureBranch&gt;</c>), computed from refs without checking anything out
    /// (09 SB-2, plan unit I1); null leaves the Feature-branch fields null and nothing else changes.
    /// "Ahead of default" is judged against this worktree's own persisted divergence base
    /// (<see cref="GetDivergenceBaseBranchAsync"/>, the Feature's actual parent branch) when one was
    /// recorded, falling back to <paramref name="defaultBranch"/> otherwise, so a nested Feature
    /// (branched from another unmerged Feature branch) is never reported as ahead by commits that
    /// already live safely on its parent branch.
    /// </summary>
    Task<WorktreeInspectionResult> InspectWorktreeAsync(
        string mainRepositoryPath,
        string worktreePath,
        string? defaultBranch,
        string? featureBranch,
        CancellationToken ct);
}
