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
}
