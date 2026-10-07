namespace GrayMoon.Worker.Models;

/// <summary>
/// Tags, local branches and origin branches taken from one <c>git for-each-ref</c> call, so a sync does not
/// pay for a separate process per list. Each list is exactly what the matching single-purpose read returns
/// (<c>GetTagsAsync</c>, <c>GetLocalBranchesAsync</c>, <c>GetRemoteBranchesFromRefsAsync</c>).
/// </summary>
/// <param name="Tags">Tag names, newest first (git's <c>creatordate</c> order).</param>
/// <param name="LocalBranches">Local branch names, sorted.</param>
/// <param name="RemoteBranches">Branch names on <c>origin</c> without the prefix, sorted, without <c>HEAD</c>.</param>
/// <param name="CheckedOutBranch">
/// The branch HEAD is attached to, when that branch exists as a ref. Null for a detached HEAD and also for an
/// unborn branch (a repository with no commits yet), which the ref listing cannot tell apart, so a null here
/// means "ask git" rather than "there is no branch".
/// </param>
/// <param name="DefaultOriginRef">
/// The repository's default branch as <c>origin/&lt;name&gt;</c>, or null when it has none - what
/// <c>GetDefaultBranchOriginRefAsync</c> returns, answered from the same listing instead of two more probes.
/// A null here is a real answer ("no default branch"), not "ask git": the listing already contains every
/// remote-tracking ref the probes would have looked at.
/// </param>
public sealed record RefSnapshot(
    IReadOnlyList<string> Tags,
    IReadOnlyList<string> LocalBranches,
    IReadOnlyList<string> RemoteBranches,
    string? CheckedOutBranch,
    string? DefaultOriginRef);
