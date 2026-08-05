namespace GrayMoon.Common.Git;

/// <summary>
/// Per-operation-kind timeouts for every git (and dotnet-gitversion) process the Agent launches through
/// <c>GitProcessRunner</c>. Every invocation is bounded by one of these tiers - there is no "no timeout"
/// option - so a hung process (stuck credential prompt, cloud-sync/antivirus file lock, unresponsive
/// network share) is killed and reported as a normal failure instead of hanging its caller's
/// worker/semaphore slot forever. No automatic retry is triggered by a timeout; existing retry-on-bad-exit-code
/// behavior for clone/fetch/pull/push/ls-remote is unchanged, just now bounded per attempt.
/// </summary>
public sealed class GitProcessOptions
{
    /// <summary>Local/fast operations: status, diff/show, rev-parse, cat-file, add, restore, reset, commit,
    /// branch/tag queries, commit counts, Git Changes stage/unstage/commit. Default 60.</summary>
    public int DefaultTimeoutSeconds { get; init; } = 60;

    /// <summary>Network operations: fetch, pull, push, ls-remote. Default 180.</summary>
    public int NetworkTimeoutSeconds { get; init; } = 180;

    /// <summary>dotnet-gitversion invocations, which scan full repository history. Default 90.</summary>
    public int GitVersionTimeoutSeconds { get; init; } = 90;

    /// <summary>
    /// Clone is intentionally its own tier, separate from <see cref="NetworkTimeoutSeconds"/>: an initial
    /// clone can legitimately take far longer than any other git operation (large repository, slow/first
    /// connection, no local objects to reuse), and killing it partway through wastes all the work done so
    /// far instead of just failing fast on a truly transient error. Default 0, meaning **no timeout** -
    /// a clone runs to completion (or failure) with no time bound. Set to a positive value to bound it.
    /// </summary>
    public int CloneTimeoutSeconds { get; init; } = 0;
}
