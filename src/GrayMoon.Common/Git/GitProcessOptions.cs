namespace GrayMoon.Common.Git;

/// <summary>
/// Per-operation-kind timeouts for every git (and dotnet-gitversion) process the Worker launches through
/// <c>GitProcessRunner</c>. Every invocation is bounded by one of these tiers - there is no "no timeout"
/// option - so a hung process (stuck credential prompt, cloud-sync/antivirus file lock, unresponsive
/// network share) is killed and reported as a normal failure instead of hanging its caller's
/// worker/semaphore slot forever. No automatic retry is triggered by a timeout; existing retry-on-bad-exit-code
/// behavior for clone/fetch/pull/push/ls-remote is unchanged, just now bounded per attempt.
/// </summary>
// The properties are settable, not init-only: the Worker is a Native AOT exe whose generated configuration binder
// skips init-only properties without an error (WorkerOptionsBindingTests).
public sealed class GitProcessOptions
{
    /// <summary>Local/fast operations: status, diff/show, rev-parse, cat-file, add, restore, reset, commit,
    /// branch/tag queries, commit counts, Git Changes stage/unstage/commit. Default 60.</summary>
    public int DefaultTimeoutSeconds { get; set; } = 60;

    /// <summary>Network operations: fetch, pull, push, ls-remote. Default 180.</summary>
    public int NetworkTimeoutSeconds { get; set; } = 180;

    /// <summary>dotnet-gitversion invocations, which scan full repository history. Default 90.</summary>
    public int GitVersionTimeoutSeconds { get; set; } = 90;

    /// <summary>
    /// Clone is intentionally its own tier, separate from <see cref="NetworkTimeoutSeconds"/>: an initial
    /// clone can legitimately take far longer than any other git operation (large repository, slow/first
    /// connection, no local objects to reuse), and killing it partway through wastes all the work done so
    /// far instead of just failing fast on a truly transient error. Default 0, meaning **no timeout** -
    /// a clone runs to completion (or failure) with no time bound. Set to a positive value to bound it.
    /// Also applies to <c>git checkout --progress</c>, which fills a new Feature worktree and takes about as long as the
    /// checkout of the original clone did.
    /// </summary>
    public int CloneTimeoutSeconds { get; set; } = 0;

    /// <summary>
    /// Clone liveness bound: a clone is killed only after it has produced no output at all (stdout or stderr) for this many
    /// seconds. Git is started with <c>--progress</c>, so a healthy clone reports progress continuously and never trips this,
    /// however long it takes overall; a stalled one is stopped and reported instead of hanging. Covers the silent phase before
    /// the first progress line too, so keep it generous. Default 180; 0 disables. Also applies to
    /// <c>git checkout --progress</c> (Feature worktree checkout).
    /// </summary>
    public int CloneIdleTimeoutSeconds { get; set; } = 180;
}
