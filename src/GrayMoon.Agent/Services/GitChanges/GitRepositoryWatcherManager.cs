using System.Collections.Concurrent;
using GrayMoon.Common.Git;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GrayMoon.Agent.Services.GitChanges;

/// <summary>
/// Reference-counted watcher leases keyed by repository path. A repository's <see cref="GitRepositoryWatcher"/>
/// is created on first lease and stays alive while at least one lease is held, plus a short idle grace
/// period after the last lease releases - so a watcher survives brief gaps between renewing operations
/// instead of being torn down and recreated on every request. When that idle grace period elapses with no
/// renewed lease (a repository/workspace was removed, or simply not viewed again), this is also the point
/// where the corresponding entries in <see cref="GitStatusRefreshCoordinator"/>, <see cref="GitChangesSnapshotCache"/>,
/// and <see cref="GitChangesRepositoryRegistry"/> are pruned - the Agent has no direct signal for "this
/// repository/workspace was deleted" (it only ever learns of a path via the App asking about it), so an
/// expired watcher lease is the process-local proxy for that removal. While a watcher entry exists, coverage
/// and recent working-tree observations are retained in process memory for that window only.
/// </summary>
public sealed class GitRepositoryWatcherManager(
    GitStatusRefreshCoordinator refreshCoordinator,
    GitChangesSnapshotCache snapshotCache,
    GitChangesRepositoryRegistry repositoryRegistry,
    IOptions<GitChangesOptions> options,
    ILoggerFactory loggerFactory,
    ILogger<GitRepositoryWatcherManager> logger) : IDisposable
{
    private readonly GitChangesOptions _options = options.Value;
    private readonly ConcurrentDictionary<string, WatcherEntry> _entries = new(StringComparer.OrdinalIgnoreCase);

    public int ActiveWatcherCount => _entries.Count;

    public IDisposable Acquire(string repoPath)
    {
        var key = GitChangesSnapshotCache.NormalizeKey(repoPath);
        var entry = _entries.GetOrAdd(key, _ => CreateEntry(repoPath));
        entry.CancelIdleDisposal();
        Interlocked.Increment(ref entry.LeaseCount);
        return new Lease(this, key);
    }

    /// <summary>Returns coverage for a currently tracked (or still-known) watcher entry, if any.</summary>
    public bool TryGetCoverage(string repoPath, out GitRepositoryWatcherCoverage? coverage)
    {
        var key = GitChangesSnapshotCache.NormalizeKey(repoPath);
        if (_entries.TryGetValue(key, out var entry))
        {
            coverage = entry.Coverage;
            return true;
        }

        coverage = null;
        return false;
    }

    /// <summary>Snapshot of recent working-tree observations for an active watcher entry.</summary>
    public bool TryGetRecentObservations(string repoPath, out IReadOnlyList<GitRepositoryObservedChange> observations)
    {
        var key = GitChangesSnapshotCache.NormalizeKey(repoPath);
        if (_entries.TryGetValue(key, out var entry))
        {
            observations = entry.Observations.Snapshot();
            return true;
        }

        observations = [];
        return false;
    }

    private WatcherEntry CreateEntry(string repoPath)
    {
        var coverage = new GitRepositoryWatcherCoverage { StartedAt = DateTimeOffset.UtcNow };
        var observations = new GitRepositoryWatcherObservationBuffer();
        var watcher = new GitRepositoryWatcher(repoPath, loggerFactory.CreateLogger<GitRepositoryWatcher>());
        watcher.Changed += () => refreshCoordinator.MarkDirty(repoPath);
        watcher.Overflowed += () =>
        {
            coverage.RecordDiscontinuity(DateTimeOffset.UtcNow);
            refreshCoordinator.MarkDirty(repoPath);
        };
        watcher.Observed += observation =>
        {
            coverage.RecordObservation(observation.ObservedAt);
            observations.Add(observation);
        };
        logger.LogDebug("Created git repository watcher for {RepoPath}", repoPath);
        return new WatcherEntry(watcher, coverage, observations);
    }

    private void Release(string key)
    {
        if (!_entries.TryGetValue(key, out var entry))
        {
            return;
        }

        var remaining = Interlocked.Decrement(ref entry.LeaseCount);
        if (remaining > 0)
        {
            return;
        }

        var graceMinutes = Math.Max(1, _options.WatcherIdleGraceMinutes);
        entry.ScheduleIdleDisposal(TimeSpan.FromMinutes(graceMinutes), () =>
        {
            if (entry.LeaseCount <= 0 && _entries.TryRemove(new KeyValuePair<string, WatcherEntry>(key, entry)))
            {
                entry.Dispose();

                // This is the point where a repository truly stops being watched/queried (no lease
                // renewed it before the idle grace period elapsed - the same signal a removed repository
                // or a deleted workspace produces, since the App simply stops asking about that path).
                // Prune the other git-changes dictionaries keyed by this same repo path so they don't
                // grow unbounded for the lifetime of the process.
                refreshCoordinator.RemoveTracker(key);
                snapshotCache.Remove(key);
                repositoryRegistry.Remove(key);

                logger.LogDebug("Disposed idle git repository watcher for {RepoPath}", key);
            }
        });
    }

    public void Dispose()
    {
        foreach (var entry in _entries.Values)
        {
            entry.Dispose();
        }

        _entries.Clear();
    }

    private sealed class WatcherEntry(
        GitRepositoryWatcher watcher,
        GitRepositoryWatcherCoverage coverage,
        GitRepositoryWatcherObservationBuffer observations) : IDisposable
    {
        public int LeaseCount;
        public GitRepositoryWatcherCoverage Coverage { get; } = coverage;
        public GitRepositoryWatcherObservationBuffer Observations { get; } = observations;
        private Timer? _idleTimer;

        public void ScheduleIdleDisposal(TimeSpan delay, Action onElapsed)
        {
            _idleTimer?.Dispose();
            _idleTimer = new Timer(_ => onElapsed(), null, delay, Timeout.InfiniteTimeSpan);
        }

        public void CancelIdleDisposal()
        {
            _idleTimer?.Dispose();
            _idleTimer = null;
        }

        public void Dispose()
        {
            _idleTimer?.Dispose();
            Coverage.MarkEnded(DateTimeOffset.UtcNow);
            watcher.Dispose();
        }
    }

    private sealed class Lease(GitRepositoryWatcherManager manager, string key) : IDisposable
    {
        private bool _released;

        public void Dispose()
        {
            if (_released)
            {
                return;
            }

            _released = true;
            manager.Release(key);
        }
    }
}
