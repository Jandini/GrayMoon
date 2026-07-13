namespace GrayMoon.Common.Git;

/// <summary>Bounded-concurrency and debounce tuning for the Git Changes feature. Configurable, benchmark-driven.</summary>
public sealed class GitChangesOptions
{
    /// <summary>Max repositories with a concurrent status scan in flight. Default 16.</summary>
    public int MaxParallelRepositoryOperations { get; init; } = 16;

    /// <summary>Max repositories with a concurrent mutation (stage/unstage/commit) in flight. Default 4.</summary>
    public int MaxParallelRepositoryMutations { get; init; } = 4;

    /// <summary>Max concurrent diff content loads. Default 4.</summary>
    public int MaxParallelDiffLoads { get; init; } = 4;

    /// <summary>Debounce window after a watcher event before running an authoritative status scan.</summary>
    public int WatcherDebounceMilliseconds { get; init; } = 400;

    /// <summary>Idle time with no renewing operation before a repository's watcher lease is disposed.</summary>
    public int WatcherIdleGraceMinutes { get; init; } = 10;

    /// <summary>
    /// How often the workspace background monitoring service (not the browser page) sweeps active
    /// workspace repositories to seed/renew their Agent-side watcher lease. Must stay comfortably below
    /// <see cref="WatcherIdleGraceMinutes"/> or leases would expire between sweeps. Default 3.
    /// </summary>
    public int WatcherRenewalIntervalMinutes { get; init; } = 3;

    /// <summary>
    /// How long a workspace stays "active" (and therefore in scope for background monitoring) after its
    /// last Git Changes page viewer navigates away or disconnects. Kept just under
    /// <see cref="WatcherIdleGraceMinutes"/> so the App stops sweeping a workspace slightly before its
    /// Agent-side watcher leases would idle out anyway. Default 8.
    /// </summary>
    public int WorkspaceActivityGraceMinutes { get; init; } = 8;
}
