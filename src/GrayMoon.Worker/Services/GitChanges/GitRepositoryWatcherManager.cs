using System.Collections.Concurrent;
using GrayMoon.Common.Git;
using GrayMoon.Worker.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GrayMoon.Worker.Services.GitChanges;

/// <summary>What <see cref="GitRepositoryWatcherManager.Touch"/> did for a repository.</summary>
public enum WatcherTouchResult
{
    /// <summary>A healthy watcher already existed; its deadline was extended.</summary>
    Renewed,

    /// <summary>No watcher existed and a healthy one was created. A refresh was queued: changes made while there was no watcher are now visible.</summary>
    Created,

    /// <summary>A degraded watcher (failed start or restart) was restarted successfully. A refresh was queued.</summary>
    Recovered,

    /// <summary>The watcher exists but its OS watcher is not running (or recovery is cooling down). The deadline was extended.</summary>
    Degraded,

    /// <summary>The folder is being removed; no watcher was created or renewed.</summary>
    UnderRemoval,
}

/// <summary>Why a watcher entry left the manager. Logged so a missing watcher can be explained.</summary>
internal enum WatcherRemovalReason
{
    IdleExpired,
    ExplicitRemoval,
    WorkerShutdown,
}

/// <summary>
/// Watcher entries keyed by repository path. A repository's <see cref="GitRepositoryWatcher"/> is created on first
/// <see cref="Acquire"/> or <see cref="Touch"/> and lives until its deadline passes with no lease held. Each
/// <see cref="Acquire"/>, <see cref="Touch"/> and release of the last lease pushes the deadline out by the idle grace
/// period, so a watcher survives gaps between renewing operations instead of being torn down and recreated on every
/// request. One sweeper removes entries whose deadline has passed. That is also the point where the matching entries in
/// <see cref="GitStatusRefreshCoordinator"/>, <see cref="GitChangesSnapshotCache"/> and
/// <see cref="GitChangesRepositoryRegistry"/> are pruned - the Worker has no direct signal for "this
/// repository/workspace was deleted" (it only ever learns of a path via the App asking about it), so an expired
/// entry is the process-local proxy for that removal. While a watcher entry exists, coverage and recent working-tree
/// observations are retained in process memory for that window only.
/// </summary>
/// <remarks>
/// Every lifetime transition (create, renew, release, expiry) happens under one lock, so an expiry and a renewal of the
/// same entry are ordered rather than racing. Leases capture their entry: releasing a lease whose entry was already
/// replaced or removed is a no-op and can never decrement a newer entry. Watchers are disposed outside the lock.
/// </remarks>
public sealed class GitRepositoryWatcherManager(
    GitStatusRefreshCoordinator refreshCoordinator,
    GitChangesSnapshotCache snapshotCache,
    GitChangesRepositoryRegistry repositoryRegistry,
    IOptions<GitChangesOptions> options,
    ILoggerFactory loggerFactory,
    ILogger<GitRepositoryWatcherManager> logger,
    IRepositoryAccess access,
    TimeProvider? timeProvider = null) : IDisposable
{
    // How often the sweeper looks for entries past their deadline. Expiry can therefore be up to this much later
    // than the idle grace, which is minutes.
    private static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(30);

    // Minimum time between restart attempts of a degraded watcher, so a folder that cannot be watched is not retried on every renewal.
    private static readonly TimeSpan RecoveryCooldown = TimeSpan.FromSeconds(30);

    private readonly GitChangesOptions _options = options.Value;
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, WatcherEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private ITimer? _sweeper;
    private bool _disposed;

    /// <summary>Number of tracked entries, healthy or not.</summary>
    public int ActiveWatcherCount => _entries.Count;

    /// <summary>Number of entries whose OS watchers are actually running.</summary>
    public int HealthyWatcherCount => _entries.Values.Count(entry => entry.IsHealthy);

    public TimeSpan IdleGrace => TimeSpan.FromMinutes(Math.Max(1, _options.WatcherIdleGraceMinutes));

    public IDisposable Acquire(string repoPath)
    {
        // A folder that is being removed must not get a watcher: its handle would block the delete.
        if (access.IsUnderRemoval(repoPath))
        {
            return NullLease.Instance;
        }

        var key = GitChangesSnapshotCache.NormalizeKey(repoPath);
        lock (_gate)
        {
            if (_disposed)
            {
                return NullLease.Instance;
            }

            var (entry, _) = EnsureEntry_NoLock(repoPath, key);
            if (entry is null)
            {
                // A removal claimed the folder between the check above and the registration.
                return NullLease.Instance;
            }

            entry.LeaseCount++;
            entry.ExpiresAt = _time.GetUtcNow() + IdleGrace;
            if (!entry.IsHealthy)
            {
                logger.LogWarning(
                    "[GCDiag] watcher entry exists but its OS watcher is not running: {RepoPath} (entries={Entries}); recovery is retried on renewal",
                    key, _entries.Count);
            }

            return new Lease(this, entry);
        }
    }

    /// <summary>
    /// Renews a repository's watcher without holding a lease and without running git: creates it when missing,
    /// restarts it when degraded, and pushes its deadline out. Safe to call on a cadence for every repository the
    /// App is showing. A created or recovered watcher queues a refresh, because changes made while nothing watched
    /// the folder are otherwise unseen.
    /// </summary>
    public WatcherTouchResult Touch(string repoPath)
    {
        if (access.IsUnderRemoval(repoPath))
        {
            return WatcherTouchResult.UnderRemoval;
        }

        var key = GitChangesSnapshotCache.NormalizeKey(repoPath);
        WatcherTouchResult result;
        lock (_gate)
        {
            if (_disposed)
            {
                return WatcherTouchResult.Degraded;
            }

            var (entry, outcome) = EnsureEntry_NoLock(repoPath, key);
            if (entry is null)
            {
                return WatcherTouchResult.UnderRemoval;
            }

            entry.ExpiresAt = Max(entry.ExpiresAt, _time.GetUtcNow() + IdleGrace);
            result = outcome;
        }

        if (result is WatcherTouchResult.Created or WatcherTouchResult.Recovered)
        {
            refreshCoordinator.MarkDirty(repoPath);
        }

        return result;
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

    /// <summary>
    /// Removes every entry whose deadline has passed and which holds no lease. Runs on the sweeper timer; internal so
    /// tests drive it with a fake clock. Returns how many entries were removed.
    /// </summary>
    internal int SweepExpired(DateTimeOffset now)
    {
        List<WatcherEntry>? expired = null;
        lock (_gate)
        {
            if (_disposed)
            {
                return 0;
            }

            foreach (var (key, entry) in _entries)
            {
                if (entry.LeaseCount <= 0
                    && entry.ExpiresAt <= now
                    && _entries.TryRemove(new KeyValuePair<string, WatcherEntry>(key, entry)))
                {
                    // Pruned under the lock so an Acquire that recreates this path right after cannot have its fresh state removed.
                    PruneCaches(key);
                    (expired ??= []).Add(entry);
                }
            }
        }

        if (expired is null)
        {
            return 0;
        }

        foreach (var entry in expired)
        {
            entry.Dispose();
            logger.LogInformation(
                "[GCDiag] watcher removed: reason={Reason} {RepoPath} (entries remaining={Entries})",
                WatcherRemovalReason.IdleExpired, entry.Key, _entries.Count);
        }

        return expired.Count;
    }

    /// <summary>Finds or creates the entry for a path and restarts it when it is degraded. Caller holds <see cref="_gate"/>.</summary>
    private (WatcherEntry? Entry, WatcherTouchResult Result) EnsureEntry_NoLock(string repoPath, string key)
    {
        var now = _time.GetUtcNow();
        if (!_entries.TryGetValue(key, out var entry))
        {
            entry = CreateEntry(repoPath, key, now);
            if (entry.IsReleased)
            {
                return (null, WatcherTouchResult.UnderRemoval);
            }

            _entries[key] = entry;
            _sweeper ??= _time.CreateTimer(_ => SweepExpired(_time.GetUtcNow()), null, SweepInterval, SweepInterval);
            return (entry, entry.IsHealthy ? WatcherTouchResult.Created : WatcherTouchResult.Degraded);
        }

        if (entry.IsHealthy)
        {
            return (entry, WatcherTouchResult.Renewed);
        }

        if (now - entry.LastRecoveryAttempt >= RecoveryCooldown)
        {
            entry.LastRecoveryAttempt = now;
            if (entry.TryRecover())
            {
                logger.LogInformation("[GCDiag] watcher recovered in place: {RepoPath}", key);
                return (entry, WatcherTouchResult.Recovered);
            }
        }

        return (entry, WatcherTouchResult.Degraded);
    }

    private WatcherEntry CreateEntry(string repoPath, string key, DateTimeOffset now)
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
        logger.LogInformation("[GCDiag] watcher entry created for {RepoPath} (entries after create={Entries})", repoPath, _entries.Count + 1);
        var entry = new WatcherEntry(key, watcher, coverage, observations, released => OnEntryReleased(key, released))
        {
            ExpiresAt = now + IdleGrace,
            LastRecoveryAttempt = now,
        };

        // The access broker lets go of this watcher when the folder is removed, and pauses it while a folder beneath it is.
        // Registering into a folder that is already claimed releases the entry on the spot (see Acquire).
        entry.Track(access.RegisterReleasable(repoPath, entry));
        if (!entry.IsReleased)
        {
            entry.Track(access.RegisterAncestorObserver(repoPath, watcher));
        }

        return entry;
    }

    // Called by the access broker, possibly while it holds its own lock, so this stays lock-free with respect to _gate.
    private void OnEntryReleased(string key, WatcherEntry entry)
    {
        if (!_entries.TryRemove(new KeyValuePair<string, WatcherEntry>(key, entry)))
        {
            return;
        }

        // Same pruning as an expired entry: nothing is watching this folder any more.
        PruneCaches(key);
        logger.LogInformation("[GCDiag] watcher removed: reason={Reason} {RepoPath}", WatcherRemovalReason.ExplicitRemoval, key);
    }

    private void PruneCaches(string key)
    {
        refreshCoordinator.RemoveTracker(key);
        snapshotCache.Remove(key);
        repositoryRegistry.Remove(key);
    }

    private void Release(WatcherEntry entry)
    {
        lock (_gate)
        {
            // A lease outlives its entry when the entry expired, was removed with its folder, or was replaced; it
            // must not touch whichever entry holds the key now.
            if (entry.IsDisposed || !_entries.TryGetValue(entry.Key, out var current) || !ReferenceEquals(current, entry))
            {
                logger.LogDebug("[GCDiag] watcher lease released for an entry that is no longer current: {RepoPath}", entry.Key);
                return;
            }

            if (entry.LeaseCount <= 0)
            {
                logger.LogWarning("[GCDiag] watcher lease count underflow for {RepoPath}: a duplicate lease release was ignored", entry.Key);
                return;
            }

            if (--entry.LeaseCount == 0)
            {
                entry.ExpiresAt = _time.GetUtcNow() + IdleGrace;
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            // Stop the sweeper before the entries go, so it cannot run against a half-disposed manager.
            _disposed = true;
            _sweeper?.Dispose();
            _sweeper = null;
        }

        foreach (var entry in _entries.Values)
        {
            entry.Dispose();
        }

        logger.LogDebug("[GCDiag] watcher manager disposed: reason={Reason} entries={Entries}", WatcherRemovalReason.WorkerShutdown, _entries.Count);
        _entries.Clear();
    }

    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a >= b ? a : b;

    private sealed class WatcherEntry(
        string key,
        GitRepositoryWatcher watcher,
        GitRepositoryWatcherCoverage coverage,
        GitRepositoryWatcherObservationBuffer observations,
        Action<WatcherEntry> onReleased) : IDisposable, IReleasable
    {
        // Mutated only under the manager's gate.
        public int LeaseCount;
        public DateTimeOffset ExpiresAt;
        public DateTimeOffset LastRecoveryAttempt;

        public string Key { get; } = key;
        public GitRepositoryWatcherCoverage Coverage { get; } = coverage;
        public GitRepositoryWatcherObservationBuffer Observations { get; } = observations;
        private readonly List<IDisposable> _registrations = [];
        private volatile bool _released;
        private int _disposed;

        public bool IsReleased => _released;

        public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

        /// <summary>True while every OS watcher this entry needs is running.</summary>
        public bool IsHealthy => watcher.IsHealthy;

        public bool TryRecover() => watcher.TryRecover();

        public void Track(IDisposable registration)
        {
            lock (_registrations)
            {
                if (Volatile.Read(ref _disposed) == 0)
                {
                    _registrations.Add(registration);
                    return;
                }
            }

            registration.Dispose();
        }

        /// <summary>Called by the access broker when the watched folder is being removed.</summary>
        public void Release()
        {
            _released = true;
            onReleased(this);
            Dispose();
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            List<IDisposable> registrations;
            lock (_registrations)
            {
                registrations = [.. _registrations];
                _registrations.Clear();
            }

            foreach (var registration in registrations)
            {
                registration.Dispose();
            }

            Coverage.MarkEnded(DateTimeOffset.UtcNow);
            watcher.Dispose();
        }
    }

    private sealed class NullLease : IDisposable
    {
        public static readonly NullLease Instance = new();

        public void Dispose()
        {
        }
    }

    private sealed class Lease(GitRepositoryWatcherManager manager, WatcherEntry entry) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0)
            {
                return;
            }

            manager.Release(entry);
        }
    }
}
