namespace GrayMoon.Worker.Models;

/// <summary>
/// Everything sync reads from a repository's local refs and commit graph, taken in one in-process pass (see
/// <c>ILocalGitSnapshotReader</c>). Detached from the repository it came from: plain values only.
/// </summary>
/// <param name="Refs">
/// Tags, branch lists, default origin ref and origin/HEAD state, with the same meaning as the CLI ref listing.
/// <see cref="RefSnapshot.CheckedOutBranch"/> keeps its meaning (the attached branch when it exists as a ref);
/// <paramref name="CurrentBranch"/> also names an unborn branch.
/// </param>
/// <param name="CurrentBranch">The branch HEAD is attached to, unborn included (what <c>git branch --show-current</c> prints); null when detached.</param>
/// <param name="IsHeadUnborn">HEAD names a branch that has no commit yet (a repository with no commits).</param>
/// <param name="HeadSha">The commit HEAD resolves to, or null when unborn.</param>
/// <param name="CheckedOutTag">
/// The tag HEAD sits exactly on when it is detached (what <c>git describe --tags --exact-match</c> prints:
/// annotated tags before lightweight ones, the newest annotated tag, otherwise the first name in ref order);
/// null when attached or unborn or when no tag points at HEAD.
/// </param>
/// <param name="DefaultBehind">Commits on the divergence ref (<c>origin/&lt;base&gt;</c>, else the default origin ref) not on HEAD; null when unknown.</param>
/// <param name="DefaultAhead">Commits on HEAD not on the divergence ref; null when unknown.</param>
/// <param name="CurrentBranchCounts">
/// The commit counts sync reports for <paramref name="CurrentBranch"/>, with exactly the meaning of
/// <c>IGitRepositoryReader.ProbeCommitCountsAsync</c> for the checked-out branch (upstream counts, or outgoing
/// against the divergence base / default when there is no usable upstream). Null when detached. Only valid for
/// <paramref name="CurrentBranch"/> and only while <see cref="RefSnapshot.DefaultOriginRef"/> is current.
/// </param>
/// <param name="GraphCalculations">How many commit-graph walks the snapshot made (for timing logs).</param>
public sealed record LocalGitSnapshot(
    RefSnapshot Refs,
    string? CurrentBranch,
    bool IsHeadUnborn,
    string? HeadSha,
    string? CheckedOutTag,
    int? DefaultBehind,
    int? DefaultAhead,
    CommitCountsProbeResult? CurrentBranchCounts,
    int GraphCalculations);
