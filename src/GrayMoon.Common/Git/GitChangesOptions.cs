namespace GrayMoon.Common.Git;

/// <summary>Bounded-concurrency and debounce tuning for the Git Changes feature. Configurable, benchmark-driven.</summary>
public sealed class GitChangesOptions
{
    /// <summary>
    /// Single shared concurrency bound for every repository-level batch operation in the Git Changes
    /// feature - status scans (background sweep, on-open warm-up, manual Refresh), stage/unstage/commit
    /// fan-out, and diff loads all gate on this one value rather than each carrying its own hard-coded
    /// limit. Default 8.
    /// </summary>
    public int MaxParallelRepositoryOperations { get; init; } = 8;

    /// <summary>Debounce window after a watcher event before running an authoritative status scan.</summary>
    public int WatcherDebounceMilliseconds { get; init; } = 400;

    /// <summary>
    /// Bound for one watcher-driven status scan (the fire-and-forget path from <c>MarkDirty</c>). Keeps a
    /// hung owning scan from pinning the per-repository refresh tracker forever even when App CancelCommand
    /// never reaches it. Individual git processes still have their own <c>GitProcessOptions</c> timeouts;
    /// this is the outer budget for the whole scan loop. Default 120.
    /// </summary>
    public int WatcherScanTimeoutSeconds { get; init; } = 120;

    /// <summary>Idle time with no renewing operation before a repository's watcher lease is disposed.</summary>
    public int WatcherIdleGraceMinutes { get; init; } = 10;

    /// <summary>
    /// How often the workspace background monitoring service (not the browser page) sweeps active
    /// workspace repositories to seed/renew their Worker-side watcher lease. Must stay comfortably below
    /// <see cref="WatcherIdleGraceMinutes"/> or leases would expire between sweeps. Default 3.
    /// An active workspace is any GrayMoon circuit currently on a <c>/workspaces/{id}/...</c> route
    /// (plus the configured workspace activity grace after leaving).
    /// </summary>
    public int WatcherRenewalIntervalMinutes { get; init; } = 3;

    /// <summary>
    /// How long a workspace stays "active" (and therefore in scope for background monitoring) after its
    /// last GrayMoon circuit leaves any <c>/workspaces/{id}/...</c> page or disconnects. Kept just under
    /// <see cref="WatcherIdleGraceMinutes"/> so the App stops sweeping a workspace slightly before its
    /// Worker-side watcher leases would idle out anyway. Default 8.
    /// </summary>
    public int WorkspaceActivityGraceMinutes { get; init; } = 8;

    /// <summary>
    /// When false (the cutover default), Git Changes snapshots are written only to the context tables.
    /// Set true to also write the legacy special-Workspace tables. Rollback is turning this back on.
    /// </summary>
    public bool WriteLegacyGitChangesTables { get; init; }

    /// <summary>
    /// Permanent <c>GitChangesBench</c> logs use Information when path count is at or above this floor
    /// (or <see cref="BenchmarkLogMinElapsedMs"/> is met); otherwise Debug. Default 500 so small
    /// workspaces do not flood Information logs.
    /// </summary>
    public int BenchmarkLogMinPathCount { get; init; } = 500;

    /// <summary>
    /// Permanent <c>GitChangesBench</c> logs use Information when an event's elapsed milliseconds are
    /// at or above this floor (or <see cref="BenchmarkLogMinPathCount"/> is met); otherwise Debug.
    /// Default 100.
    /// </summary>
    public int BenchmarkLogMinElapsedMs { get; init; } = 100;

    /// <summary>
    /// Internal hydration switch only (not a product inventory cap). At or below this total entry count
    /// the Changes page uses today's single-shot full hydration and default expand. Above it, summary-first
    /// open + lazy folder pages. Tuned from M0 (~1.5–2K is where full-circuit reload storms hurt). Default 1_500.
    /// </summary>
    public int FullHydrationMaxEntries { get; init; } = 1_500;

    /// <summary>
    /// Rows fetched per request when expanding a folder or paging the virtualizer in large mode.
    /// Scroll/expand continues until the prefix is exhausted. Default 200.
    /// </summary>
    public int TreePageSize { get; init; } = 200;

    /// <summary>
    /// Depth of folder summaries computed at persist time for Layer A badges (Phase 3). Default 2.
    /// </summary>
    public int FolderRollupDepth { get; init; } = 2;

    /// <summary>
    /// Soft bound on retained flattened tree rows in large mode (evict collapsed page caches).
    /// Must stay at least <see cref="TreePageSize"/> plus overscan. Default 500.
    /// </summary>
    public int CircuitVisibleRowBudget { get; init; } = 500;

    /// <summary>
    /// Per-repo paths inlined on one Worker→App SignalR message before chunked transfer (Phase 4).
    /// Placeholder until a byte budget is chosen from M0. Default 2_000.
    /// </summary>
    public int SnapshotPathsInlineMax { get; init; } = 2_000;

    /// <summary>True when <paramref name="totalEntries"/> should use summary-first + lazy pages.</summary>
    public bool UseLargeHydration(int totalEntries) =>
        totalEntries > Math.Max(0, FullHydrationMaxEntries);
}
