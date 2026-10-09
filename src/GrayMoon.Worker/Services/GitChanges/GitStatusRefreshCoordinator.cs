using System.Collections.Concurrent;
using System.Diagnostics;
using GrayMoon.Worker.Abstractions;
using GrayMoon.Common.Git;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GrayMoon.Worker.Services.GitChanges;

/// <summary>Clean/Dirty/Refreshing/RefreshingAndDirty/Disposed per-repository refresh state.</summary>
public enum RepositoryRefreshState
{
    Clean,
    Dirty,
    Refreshing,
    RefreshingAndDirty,
    Disposed,
}

/// <summary>
/// Per-repository debounce coordinator, backed by a global bounded semaphore shared across all
/// repositories (<see cref="GitChangesOptions.MaxParallelRepositoryOperations"/>, default 8). A watcher
/// event marks a repository dirty; after a debounce window, exactly one authoritative git status scan
/// runs. A dirty event that arrives while a scan is already in flight is coalesced into a single
/// follow-up scan - a repository never has more than one active and one pending refresh.
/// </summary>
public sealed class GitStatusRefreshCoordinator : IDisposable, IGitChangesRefreshSuppressor
{
    private readonly IRepositoryGitChangesService _gitChangesService;
    private readonly GitChangesSnapshotCache _snapshotCache;
    private readonly GitChangesOptions _options;
    private readonly ILogger<GitStatusRefreshCoordinator> _logger;
    private readonly RepositoryPathGate? _pathGate;
    private readonly SemaphoreSlim _statusScanGate;
    private readonly ConcurrentDictionary<string, RepositoryRefreshTracker> _trackers = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    public GitStatusRefreshCoordinator(
        IRepositoryGitChangesService gitChangesService,
        GitChangesSnapshotCache snapshotCache,
        IOptions<GitChangesOptions> options,
        ILogger<GitStatusRefreshCoordinator> logger,
        RepositoryPathGate? pathGate = null)
    {
        _pathGate = pathGate;
        _gitChangesService = gitChangesService;
        _snapshotCache = snapshotCache;
        _options = options.Value;
        _logger = logger;
        _statusScanGate = new SemaphoreSlim(Math.Max(1, _options.MaxParallelRepositoryOperations));
    }

    /// <summary>Raised on the thread that completes a scan, with the repository path and the new snapshot.</summary>
    public event Action<string, GitChangeSnapshot>? SnapshotReady;

    /// <summary>Marks a repository dirty from a watcher event and schedules a debounced scan. Fire-and-forget.</summary>
    public void MarkDirty(string repoPath)
    {
        if (_disposed || _pathGate?.IsBlocked(repoPath) == true)
        {
            return;
        }

        var tracker = GetOrAddTracker(repoPath);
        tracker.ScheduleDebouncedRefresh(_options.WatcherDebounceMilliseconds, () => _ = RunWatcherScanAsync(repoPath, tracker));
    }

    /// <summary>Immediate scan for manual refresh / on-demand status requests. Bypasses any pending debounce
    /// timer but still coalesces with a scan that is already in flight for the same repository.</summary>
    public async Task<GitChangeStatusResult> RefreshNowAsync(string repoPath, CancellationToken cancellationToken, bool includeLineStats = false)
    {
        if (_pathGate?.IsBlocked(repoPath) == true)
        {
            return PathReleasedResult();
        }

        var tracker = GetOrAddTracker(repoPath);
        tracker.NoteManualRefresh();
        var result = await RunScanAsync(repoPath, tracker, cancellationToken, includeLineStats);
        if (includeLineStats && result.Success && result.Snapshot != null && result.Snapshot.Insertions is null)
        {
            var version = _snapshotCache.NextVersion(repoPath);
            var filled = await _gitChangesService.GetStatusAsync(repoPath, version, cancellationToken, includeLineStats: true);
            if (filled.Success && filled.Snapshot != null)
            {
                _snapshotCache.SetLatest(repoPath, filled.Snapshot);
                SnapshotReady?.Invoke(repoPath, filled.Snapshot);
                return filled;
            }
        }

        return result;
    }

    private static GitChangeStatusResult PathReleasedResult() => new()
    {
        Success = false,
        ErrorCode = "PathReleased",
        ErrorMessage = "Repository folder is being removed.",
    };

    /// <summary>
    /// Waits until no scan is running for any repository at or beneath <paramref name="roots"/> (a running scan's
    /// git process pins its working directory), giving up after <paramref name="timeout"/>.
    /// </summary>
    public async Task DrainScansUnderAsync(IReadOnlyCollection<string> roots, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        while (_trackers.Any(t => RepositoryPathGate.IsUnder(t.Key, roots)
                                  && t.Value.State is RepositoryRefreshState.Refreshing or RepositoryRefreshState.RefreshingAndDirty))
        {
            if (Stopwatch.GetElapsedTime(started) > timeout)
            {
                _logger.LogWarning("Timed out waiting for git status scans to finish under {Roots}", string.Join("; ", roots));
                return;
            }

            await Task.Delay(50, cancellationToken);
        }
    }

    public RepositoryRefreshState GetState(string repoPath) => GetOrAddTracker(repoPath).State;

    /// <inheritdoc />
    public IDisposable BeginExternalRepositoryMutation(string repoPath)
    {
        // The scope holds the tracker instance rather than looking it up again, so a RemoveTracker while the
        // operation runs cannot strand the suppression count on a tracker nobody will release.
        var tracker = GetOrAddTracker(repoPath);
        tracker.BeginSuppression();
        _logger.LogDebug("Git Changes refresh suppression entered for {RepoPath}", repoPath);
        return new SuppressionScope(this, repoPath, tracker);
    }

    private void EndSuppression(string repoPath, RepositoryRefreshTracker tracker)
    {
        var end = tracker.EndSuppression();
        if (!end.Released)
        {
            return;
        }

        // One debounced scan, through the same path a watcher event takes, so a watcher event that lands just
        // after the last git process exits is absorbed into it instead of starting another.
        var scheduled = end.Dirtied && !_disposed;
        if (scheduled)
        {
            tracker.ScheduleDebouncedRefresh(
                _options.WatcherDebounceMilliseconds,
                () => _ = RunWatcherScanAsync(repoPath, tracker));
        }

        _logger.LogDebug(
            "Git Changes refresh suppression released for {RepoPath}: duration={DurationMs}ms, watcherRefreshesCoalesced={Coalesced}, manualRefreshBypasses={Bypasses}, authoritativeRefreshScheduled={Scheduled}",
            repoPath, (long)Stopwatch.GetElapsedTime(end.StartedAt).TotalMilliseconds, end.Coalesced, end.ManualBypasses, scheduled);
    }

    private sealed class SuppressionScope(GitStatusRefreshCoordinator owner, string repoPath, RepositoryRefreshTracker tracker) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                owner.EndSuppression(repoPath, tracker);
            }
        }
    }

    /// <summary>
    /// Drops the refresh tracker for a repository that is no longer being watched (its
    /// <see cref="GitRepositoryWatcherManager"/> lease has expired), so this dictionary does not grow
    /// unbounded for the lifetime of the process as repositories/workspaces are added and removed. Safe to
    /// call while a scan for this repository is in flight: the running <see cref="ExecuteScanLoopAsync"/>
    /// loop holds its own reference to the tracker instance (not a dictionary lookup) and runs to
    /// completion normally; a later <see cref="MarkDirty"/>/<see cref="RefreshNowAsync"/> for the same path
    /// simply starts a fresh tracker.
    /// </summary>
    public void RemoveTracker(string repoPath)
    {
        if (_trackers.TryRemove(GitChangesSnapshotCache.NormalizeKey(repoPath), out var tracker))
        {
            tracker.Dispose();
        }
    }

    public void Dispose()
    {
        _disposed = true;
        foreach (var tracker in _trackers.Values)
        {
            tracker.Dispose();
        }

        _statusScanGate.Dispose();
    }

    private RepositoryRefreshTracker GetOrAddTracker(string repoPath) =>
        _trackers.GetOrAdd(GitChangesSnapshotCache.NormalizeKey(repoPath), _ => new RepositoryRefreshTracker());

    private async Task RunWatcherScanAsync(string repoPath, RepositoryRefreshTracker tracker)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(1, _options.WatcherScanTimeoutSeconds)));
        try
        {
            await RunScanAsync(repoPath, tracker, cts.Token);
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Watcher-driven git status scan cancelled or timed out for {RepoPath}", repoPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Watcher-driven git status scan failed for {RepoPath}", repoPath);
        }
    }

    private async Task<GitChangeStatusResult> RunScanAsync(string repoPath, RepositoryRefreshTracker tracker, CancellationToken cancellationToken, bool includeLineStats = false)
    {
        if (!tracker.TryBeginRefresh(out var coalescedTask))
        {
            if (coalescedTask != null)
            {
                // Observe the caller's token so CancelCommand frees this worker without aborting the owning scan.
                return await coalescedTask.WaitAsync(cancellationToken);
            }

            return new GitChangeStatusResult { Success = false, ErrorCode = "RepositoryDisposed", ErrorMessage = "Repository is no longer being monitored." };
        }

        return await ExecuteScanLoopAsync(repoPath, tracker, cancellationToken, includeLineStats);
    }

    /// <summary>
    /// Runs the scan, then loops for as many immediate follow-ups as <see cref="RepositoryRefreshTracker.EndRefresh"/>
    /// reports pending. A follow-up must not go back through <see cref="RepositoryRefreshTracker.TryBeginRefresh"/> -
    /// the tracker is already in the Refreshing state at that point, so a re-check would mistake the
    /// follow-up for a duplicate concurrent caller and coalesce it into a no-op instead of running it.
    /// </summary>
    private async Task<GitChangeStatusResult> ExecuteScanLoopAsync(string repoPath, RepositoryRefreshTracker tracker, CancellationToken cancellationToken, bool includeLineStats)
    {
        try
        {
            GitChangeStatusResult result;
            while (true)
            {
                await _statusScanGate.WaitAsync(cancellationToken);
                try
                {
                    var version = _snapshotCache.NextVersion(repoPath);
                    // The folder is being deleted: a git process started here would pin it as its working directory.
                    result = _pathGate?.IsBlocked(repoPath) == true
                        ? PathReleasedResult()
                        : await _gitChangesService.GetStatusAsync(repoPath, version, cancellationToken, includeLineStats);
                    if (result.ErrorCode == "PathReleased")
                    {
                        // Not a failure worth logging; the removal that blocked this path owns the outcome.
                    }
                    else if (result.Success && result.Snapshot != null)
                    {
                        _snapshotCache.SetLatest(repoPath, result.Snapshot);
                        SnapshotReady?.Invoke(repoPath, result.Snapshot);
                    }
                    else
                    {
                        _logger.LogWarning("Git status scan failed for {RepoPath}: {ErrorCode} {ErrorMessage}", repoPath, result.ErrorCode, result.ErrorMessage);
                    }
                }
                finally
                {
                    _statusScanGate.Release();
                }

                var runFollowUp = tracker.EndRefresh(result, out var completionToSignal);
                completionToSignal?.TrySetResult(result);

                if (!runFollowUp)
                {
                    break;
                }
            }

            return result;
        }
        catch (Exception)
        {
            // Cancel/exception before EndRefresh used to leave the tracker stuck in Refreshing forever;
            // coalesced waiters then hung until process restart. Always reset and unblock them.
            var scheduleFollowUp = tracker.AbortRefresh(out var completionToSignal);
            completionToSignal?.TrySetCanceled();
            if (scheduleFollowUp && !_disposed)
            {
                tracker.ScheduleDebouncedRefresh(
                    _options.WatcherDebounceMilliseconds,
                    () => _ = RunWatcherScanAsync(repoPath, tracker));
            }

            throw;
        }
    }
}

/// <summary>Per-repository refresh state machine with a single debounce timer and at-most-one-pending coalescing.</summary>
internal sealed class RepositoryRefreshTracker : IDisposable
{
    private readonly object _gate = new();
    private RepositoryRefreshState _state = RepositoryRefreshState.Clean;
    private Timer? _debounceTimer;
    private Action? _onDebounceElapsed;
    private int _debounceGeneration;
    private TaskCompletionSource<GitChangeStatusResult>? _pendingCompletion;

    // Sync suppression: open scopes, whether a watcher event was swallowed while any were open, and per-episode
    // counters for the release log line. An episode runs from the first scope opening to the last one closing.
    private int _suppressionCount;
    private bool _dirtiedWhileSuppressed;
    private int _coalescedEvents;
    private int _manualBypasses;
    private long _suppressionStartedAt;

    /// <summary>What closing a suppression scope left behind. Counters are meaningful only when <see cref="Released"/>.</summary>
    public readonly record struct SuppressionEnd(bool Released, bool Dirtied, int Coalesced, int ManualBypasses, long StartedAt);

    public void BeginSuppression()
    {
        lock (_gate)
        {
            if (_suppressionCount++ == 0)
            {
                _dirtiedWhileSuppressed = false;
                _coalescedEvents = 0;
                _manualBypasses = 0;
                _suppressionStartedAt = Stopwatch.GetTimestamp();
            }
        }
    }

    public SuppressionEnd EndSuppression()
    {
        lock (_gate)
        {
            if (_suppressionCount == 0 || --_suppressionCount > 0)
            {
                return default;
            }

            var end = new SuppressionEnd(true, _dirtiedWhileSuppressed, _coalescedEvents, _manualBypasses, _suppressionStartedAt);
            _dirtiedWhileSuppressed = false;
            return end;
        }
    }

    /// <summary>Counts a manual refresh that ran while a suppression scope was open (it is never suppressed).</summary>
    public void NoteManualRefresh()
    {
        lock (_gate)
        {
            if (_suppressionCount > 0)
            {
                _manualBypasses++;
            }
        }
    }

    public RepositoryRefreshState State
    {
        get { lock (_gate) { return _state; } }
    }

    public void ScheduleDebouncedRefresh(int debounceMilliseconds, Action onDebounceElapsed)
    {
        lock (_gate)
        {
            // A Sync is rewriting this repository's .git metadata: remember that something changed and let the
            // end of the scope schedule the one refresh, rather than scanning (or queueing a follow-up) per event.
            if (_suppressionCount > 0 && _state != RepositoryRefreshState.Disposed)
            {
                _dirtiedWhileSuppressed = true;
                _coalescedEvents++;
                return;
            }

            switch (_state)
            {
                case RepositoryRefreshState.Disposed:
                    return;
                case RepositoryRefreshState.Refreshing:
                    _state = RepositoryRefreshState.RefreshingAndDirty;
                    return;
                case RepositoryRefreshState.RefreshingAndDirty:
                    // Follow-up scan already pending; nothing new to do.
                    return;
                case RepositoryRefreshState.Dirty:
                    // Reset the same timer so the scan runs after the last event, not the first.
                    // Disposing and replacing the timer drops the callback on a busy thread pool
                    // (CI never observes a scan) and can also let a slow burst start a second scan.
                    ArmDebounce_NoLock(debounceMilliseconds, onDebounceElapsed);
                    return;
            }

            _state = RepositoryRefreshState.Dirty;
            ArmDebounce_NoLock(debounceMilliseconds, onDebounceElapsed);
        }
    }

    /// <summary>Caller holds <see cref="_gate"/>.</summary>
    private void ArmDebounce_NoLock(int debounceMilliseconds, Action onDebounceElapsed)
    {
        _onDebounceElapsed = onDebounceElapsed;
        _debounceGeneration++;
        // Created disarmed so it cannot fire before this field assignment, then armed with Change.
        _debounceTimer ??= new Timer(
            static state => ((RepositoryRefreshTracker)state!).OnDebounceTimer(),
            this,
            Timeout.Infinite,
            Timeout.Infinite);
        _debounceTimer.Change(debounceMilliseconds, Timeout.Infinite);
    }

    private void OnDebounceTimer()
    {
        Action? callback;
        var generation = 0;
        lock (_gate)
        {
            if (_state != RepositoryRefreshState.Dirty)
            {
                return;
            }

            // A timer armed before the scope opened: keep the repository Dirty without a timer; the scope's end
            // re-arms it.
            if (_suppressionCount > 0)
            {
                _dirtiedWhileSuppressed = true;
                _coalescedEvents++;
                return;
            }

            generation = _debounceGeneration;
            callback = _onDebounceElapsed;
        }

        // A MarkDirty that reset the window can land between the check above and the scan.
        // Starting that scan would be the burst's first event, and the later events would queue a follow-up.
        lock (_gate)
        {
            if (_suppressionCount > 0 && _state == RepositoryRefreshState.Dirty)
            {
                _dirtiedWhileSuppressed = true;
                _coalescedEvents++;
                return;
            }

            if (_state != RepositoryRefreshState.Dirty || generation != _debounceGeneration)
            {
                return;
            }
        }

        callback?.Invoke();
    }

    /// <summary>Caller holds <see cref="_gate"/>.</summary>
    private void DisarmDebounce_NoLock()
    {
        _debounceGeneration++;
        _debounceTimer?.Change(Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>Attempts to move into Refreshing. Returns false when a scan is already in flight; in that case
    /// <paramref name="coalescedTask"/> resolves to the result of whichever scan completes next.</summary>
    public bool TryBeginRefresh(out Task<GitChangeStatusResult>? coalescedTask)
    {
        lock (_gate)
        {
            if (_state is RepositoryRefreshState.Refreshing or RepositoryRefreshState.RefreshingAndDirty)
            {
                _state = RepositoryRefreshState.RefreshingAndDirty;
                _pendingCompletion ??= new TaskCompletionSource<GitChangeStatusResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                coalescedTask = _pendingCompletion.Task;
                return false;
            }

            if (_state == RepositoryRefreshState.Disposed)
            {
                coalescedTask = null;
                return false;
            }

            DisarmDebounce_NoLock();
            _state = RepositoryRefreshState.Refreshing;
            coalescedTask = null;
            return true;
        }
    }

    /// <summary>Ends the current scan. Returns true when a follow-up scan must run immediately because a dirty
    /// event arrived while this scan was in flight. A coalesced caller's completion is carried forward to the
    /// follow-up scan rather than resolved with this scan's (pre-follow-up) result, so it always reflects state
    /// at or after the moment it started waiting.</summary>
    public bool EndRefresh(GitChangeStatusResult result, out TaskCompletionSource<GitChangeStatusResult>? completionToSignal)
    {
        lock (_gate)
        {
            if (_state == RepositoryRefreshState.RefreshingAndDirty)
            {
                _state = RepositoryRefreshState.Refreshing;
                completionToSignal = null;
                return true;
            }

            completionToSignal = _pendingCompletion;
            _pendingCompletion = null;

            if (_state != RepositoryRefreshState.Disposed)
            {
                _state = RepositoryRefreshState.Clean;
            }

            return false;
        }
    }

    /// <summary>
    /// Resets after cancel/exception mid-scan. Completes any coalesced waiters so they do not hang, and
    /// returns true when a dirty event arrived during the aborted scan (caller should schedule one
    /// debounced follow-up rather than leaving changes unseen).
    /// </summary>
    public bool AbortRefresh(out TaskCompletionSource<GitChangeStatusResult>? completionToSignal)
    {
        lock (_gate)
        {
            var wasDirty = _state == RepositoryRefreshState.RefreshingAndDirty;
            completionToSignal = _pendingCompletion;
            _pendingCompletion = null;

            if (_state != RepositoryRefreshState.Disposed)
            {
                _state = RepositoryRefreshState.Clean;
            }

            return wasDirty;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _debounceTimer?.Dispose();
            _debounceTimer = null;
            _pendingCompletion?.TrySetCanceled();
            _state = RepositoryRefreshState.Disposed;
        }
    }
}
