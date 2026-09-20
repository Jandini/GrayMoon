namespace GrayMoon.Common.Git;

/// <summary>
/// How a local branch relates to Git worktree occupancy (path / branch / HEAD), without GrayMoon Feature ownership.
/// App-layer Feature vs External classification builds on this using DB metadata.
/// </summary>
public enum GitWorktreeBranchOccupancyKind
{
    /// <summary>Branch is not checked out in any listed worktree.</summary>
    None = 0,

    /// <summary>Branch is checked out at the caller's current / special worktree path.</summary>
    Current = 1,

    /// <summary>Branch is checked out in a linked worktree other than the current path.</summary>
    OccupiedElsewhere = 2,
}

/// <summary>
/// Helpers that classify branch occupancy from a worktree inventory (path, branch, HEAD).
/// </summary>
public static class GitWorktreeOccupancy
{
    /// <summary>
    /// Finds the first worktree whose short branch name matches <paramref name="branchName"/>.
    /// Detached and bare entries never match.
    /// </summary>
    public static GitWorktreeInfo? FindByBranch(IEnumerable<GitWorktreeInfo>? worktrees, string? branchName)
    {
        if (worktrees == null || string.IsNullOrWhiteSpace(branchName))
            return null;

        foreach (var wt in worktrees)
        {
            if (wt.IsBare || wt.IsDetached)
                continue;
            if (string.Equals(wt.BranchName, branchName, StringComparison.OrdinalIgnoreCase))
                return wt;
        }

        return null;
    }

    /// <summary>Finds the worktree whose path equals <paramref name="worktreePath"/> (full-path comparison).</summary>
    public static GitWorktreeInfo? FindByPath(IEnumerable<GitWorktreeInfo>? worktrees, string? worktreePath)
    {
        if (worktrees == null || string.IsNullOrWhiteSpace(worktreePath))
            return null;

        string full;
        try
        {
            full = Path.GetFullPath(worktreePath);
        }
        catch
        {
            return null;
        }

        foreach (var wt in worktrees)
        {
            if (string.IsNullOrWhiteSpace(wt.WorktreePath))
                continue;
            if (PathsEqual(wt.WorktreePath, full))
                return wt;
        }

        return null;
    }

    /// <summary>
    /// Classifies whether <paramref name="branchName"/> is free, current, or occupied elsewhere
    /// relative to <paramref name="currentWorktreePath"/>.
    /// </summary>
    public static GitWorktreeBranchOccupancyKind ClassifyBranch(
        IEnumerable<GitWorktreeInfo>? worktrees,
        string? branchName,
        string? currentWorktreePath)
    {
        var occupied = FindByBranch(worktrees, branchName);
        if (occupied == null || string.IsNullOrWhiteSpace(occupied.WorktreePath))
            return GitWorktreeBranchOccupancyKind.None;

        if (!string.IsNullOrWhiteSpace(currentWorktreePath) && PathsEqual(occupied.WorktreePath, currentWorktreePath))
            return GitWorktreeBranchOccupancyKind.Current;

        return GitWorktreeBranchOccupancyKind.OccupiedElsewhere;
    }

    /// <summary>
    /// True when an existing worktree at <paramref name="worktreePath"/> already has the expected
    /// branch and (when provided) HEAD SHA - used for create-idempotency.
    /// </summary>
    public static bool MatchesExpected(
        GitWorktreeInfo? worktree,
        string? expectedBranchName,
        string? expectedHeadSha = null)
    {
        if (worktree == null || string.IsNullOrWhiteSpace(expectedBranchName))
            return false;

        if (!string.Equals(worktree.BranchName, expectedBranchName, StringComparison.OrdinalIgnoreCase))
            return false;

        if (string.IsNullOrWhiteSpace(expectedHeadSha))
            return true;

        return string.Equals(worktree.HeadSha, expectedHeadSha, StringComparison.OrdinalIgnoreCase);
    }

    public static bool PathsEqual(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            return false;

        try
        {
            var a = Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var b = Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
