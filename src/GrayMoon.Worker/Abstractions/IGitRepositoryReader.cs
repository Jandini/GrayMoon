using GrayMoon.Worker.Models;
using GrayMoon.Common.Git;
using GrayMoon.Worker.Services;

namespace GrayMoon.Worker.Abstractions;

/// <summary>
/// Local, read-only repository inspection. Answers questions about repository state without mutating it and
/// without contacting a remote, and exposes no backend detail (no process lanes, no lock intents), so the
/// backing implementation can change (git CLI today) without touching callers.
/// Network lookups (<c>ls-remote</c>), origin/HEAD repair and anything that writes stay on <see cref="IGitService"/>.
/// </summary>
public interface IGitRepositoryReader
{
    /// <summary>Gets the current branch name (e.g. "main"), or null when detached, unborn or unreadable.</summary>
    Task<string?> GetCurrentBranchNameAsync(string repoPath, CancellationToken ct);

    /// <summary>Returns the full SHA of HEAD, or null when the repo is missing/unborn or the command fails.</summary>
    Task<string?> GetHeadCommitAsync(string repoPath, CancellationToken ct);

    /// <summary>
    /// Returns short names of local (<c>refs/heads/&lt;name&gt;</c>) and remote-tracking (<c>refs/remotes/*/&lt;name&gt;</c>)
    /// refs that collide with <paramref name="branchName"/>, including refs nested under it (e.g. <c>name/sub</c>).
    /// Empty when none exist or the repository cannot be read.
    /// </summary>
    Task<IReadOnlyList<string>> FindBranchCollisionsAsync(string repoPath, string branchName, CancellationToken ct);

    /// <summary>Returns the full SHA for <paramref name="rev"/> (e.g. <c>origin/main</c>), or null on failure.</summary>
    Task<string?> RevParseAsync(string repoPath, string rev, CancellationToken ct);

    /// <summary>True when <paramref name="revision"/> resolves to an existing object/ref.</summary>
    Task<bool> RefExistsAsync(string repoPath, string revision, CancellationToken ct);

    /// <summary>The configured upstream of local branch <paramref name="branchName"/> (e.g. <c>origin/feature</c>), or null.</summary>
    Task<string?> GetUpstreamRefAsync(string repoPath, string branchName, CancellationToken ct);

    Task<string?> GetRemoteOriginUrlAsync(string repoPath, CancellationToken ct);

    /// <summary>
    /// Returns (outgoing count, incoming count, hasUpstream) for the current branch vs its upstream.
    /// When the branch has no upstream (or the remote upstream ref is missing): if the worktree has a
    /// Feature divergence base, outgoing is ahead of that local parent branch; otherwise outgoing is
    /// ahead of <paramref name="defaultBranchOriginRef"/> / the default origin branch. Incoming is null
    /// and hasUpstream is false in those cases.
    /// </summary>
    Task<(int? Outgoing, int? Incoming, bool HasUpstream)> GetCommitCountsAsync(string repoPath, string branchName, string? defaultBranchOriginRef, CancellationToken ct, bool skipUpstreamCheck = false);

    /// <summary>Same work as <see cref="GetCommitCountsAsync"/> but also reports whether the counts and the upstream flag could be determined at all, so callers can leave persisted values alone instead of overwriting them with nulls after a failed git command.</summary>
    Task<CommitCountsProbeResult> ProbeCommitCountsAsync(string repoPath, string branchName, string? defaultBranchOriginRef, CancellationToken ct, bool skipUpstreamCheck = false);

    /// <summary>Returns (behind, ahead, defaultBranchName) for the current branch vs the default branch. DefaultBranchName is without "origin/" prefix. When <paramref name="defaultBranchOriginRef"/> is provided, uses it instead of resolving.</summary>
    Task<(int? DefaultBehind, int? DefaultAhead, string? DefaultBranchName)> GetCommitCountsVsDefaultAsync(string repoPath, string? defaultBranchOriginRef, CancellationToken ct);

    /// <summary>Gets all local branch names (without 'origin/' prefix).</summary>
    Task<IReadOnlyList<string>> GetLocalBranchesAsync(string repoPath, CancellationToken ct);

    /// <summary>
    /// Tags, local branches, origin branches and the checked-out branch from a single query, with the
    /// same contents and order as <see cref="GetTagsAsync"/>, <see cref="GetLocalBranchesAsync"/> and
    /// <see cref="GetRemoteBranchesFromRefsAsync"/>. Returns null when the repository is missing or the query failed, in which case use
    /// the single-purpose reads. Deliberately coarse-grained: do not split it back into many small reads.
    /// </summary>
    Task<RefSnapshot?> GetRefSnapshotAsync(string repoPath, CancellationToken ct);

    /// <summary>Gets all remote branch names from local refs (refs/remotes/origin). Use after fetch; no network.</summary>
    Task<IReadOnlyList<string>> GetRemoteBranchesFromRefsAsync(string repoPath, CancellationToken ct);

    /// <summary>Gets the default branch name (e.g., "main" or "master") without "origin/" prefix, resolved from local refs.</summary>
    Task<string?> GetDefaultBranchNameAsync(string repoPath, CancellationToken ct);

    /// <summary>Gets the default branch origin ref (e.g., "origin/main") for passing to the commit-count reads to avoid resolving twice.</summary>
    Task<string?> GetDefaultBranchOriginRefAsync(string repoPath, CancellationToken ct);

    /// <summary>Gets all tag names in the repository (newest first when supported, then alphabetical).</summary>
    Task<IReadOnlyList<string>> GetTagsAsync(string repoPath, CancellationToken ct);

    /// <summary>Returns the tag name HEAD points to when the repo is in a detached HEAD state AND that commit is the exact tip of a tag; otherwise null.</summary>
    Task<string?> GetCheckedOutTagAsync(string repoPath, CancellationToken ct);

    /// <summary>Reads the worktree-local divergence base branch name (Feature PR parent), or null when unset (hooks then use default).</summary>
    Task<string?> GetDivergenceBaseBranchAsync(string repoPath, CancellationToken ct);
}
