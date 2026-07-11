using System.Collections.Concurrent;
using GrayMoon.Agent.Abstractions;
using GrayMoon.Common.Git;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GrayMoon.Agent.Services.GitChanges;

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
/// repositories (<see cref="GitChangesOptions.MaxParallelRepositoryOperations"/>, default 16). A watcher
/// event marks a repository dirty; after a debounce window, exactly one authoritative git status scan
/// runs. A dirty event that arrives while a scan is already in flight is coalesced into a single
/// follow-up scan - a repository never has more than one active and one pending refresh.
/// </summary>
public sealed class GitStatusRefreshCoordinator : IDisposable
{
    private readonly IRepositoryGitChangesService _gitChangesService;
    private readonly GitChangesSnapshotCache _snapshotCache;
    private readonly GitChangesOptions _options;
    private readonly ILogger<GitStatusRefreshCoordinator> _logger;
    private readonly SemaphoreSlim _statusScanGate;
    private readonly ConcurrentDictionary<string, RepositoryRefreshTracker> _trackers = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    public GitStatusRefreshCoordinator(
        IRepositoryGitChangesService gitChangesService,
        GitChangesSnapshotCache snapshotCache,
        IOptions<GitChangesOptions> options,
        ILogger<GitStatusRefreshCoordinator> logger)
    {
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
        if (_disposed)
        {
            return;
        }

        var tracker = GetOrAddTracker(repoPath);
        tracker.ScheduleDebouncedRefresh(_options.WatcherDebounceMilliseconds, () => _ = RunScanAsync(repoPath, tracker, CancellationToken.None));
    }

    /// <summary>Immediate scan for manual refresh / on-demand status requests. Bypasses any pending debounce
    /// timer but still coalesces with a scan that is already in flight for the same repository.</summary>
    public Task<GitChangeStatusResult> RefreshNowAsync(string repoPath, CancellationToken cancellationToken)
    {
        var tracker = GetOrAddTracker(repoPath);
        return RunScanAsync(repoPath, tracker, cancellationToken);
    }

    public RepositoryRefreshState GetState(string repoPath) => GetOrAddTracker(repoPath).State;

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

    private async Task<GitChangeStatusResult> RunScanAsync(string repoPath, RepositoryRefreshTracker tracker, CancellationToken cancellationToken)
    {
        if (!tracker.TryBeginRefresh(out var coalescedTask))
        {
            if (coalescedTask != null)
            {
                return await coalescedTask;
            }

            return new GitChangeStatusResult { Success = false, ErrorCode = "RepositoryDisposed", ErrorMessage = "Repository is no longer being monitored." };
        }

        return await ExecuteScanLoopAsync(repoPath, tracker, cancellationToken);
    }

    /// <summary>
    /// Runs the scan, then loops for as many immediate follow-ups as <see cref="RepositoryRefreshTracker.EndRefresh"/>
    /// reports pending. A follow-up must not go back through <see cref="RepositoryRefreshTracker.TryBeginRefresh"/> -
    /// the tracker is already in the Refreshing state at that point, so a re-check would mistake the
    /// follow-up for a duplicate concurrent caller and coalesce it into a no-op instead of running it.
    /// </summary>
    private async Task<GitChangeStatusResult> ExecuteScanLoopAsync(string repoPath, RepositoryRefreshTracker tracker, CancellationToken cancellationToken)
    {
        GitChangeStatusResult result;
        while (true)
        {
            await _statusScanGate.WaitAsync(cancellationToken);
            try
            {
                var version = _snapshotCache.NextVersion(repoPath);
                result = await _gitChangesService.GetStatusAsync(repoPath, version, cancellationToken);
                if (result.Success && result.Snapshot != null)
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
}

/// <summary>Per-repository refresh state machine with a single debounce timer and at-most-one-pending coalescing.</summary>
internal sealed class RepositoryRefreshTracker : IDisposable
{
    private readonly object _gate = new();
    private RepositoryRefreshState _state = RepositoryRefreshState.Clean;
    private Timer? _debounceTimer;
    private TaskCompletionSource<GitChangeStatusResult>? _pendingCompletion;

    public RepositoryRefreshState State
    {
        get { lock (_gate) { return _state; } }
    }

    public void ScheduleDebouncedRefresh(int debounceMilliseconds, Action onDebounceElapsed)
    {
        lock (_gate)
        {
            switch (_state)
            {
                case RepositoryRefreshState.Disposed:
                    return;
                case RepositoryRefreshState.Refreshing:
                    _state = RepositoryRefreshState.RefreshingAndDirty;
                    return;
                case RepositoryRefreshState.Dirty:
                case RepositoryRefreshState.RefreshingAndDirty:
                    // Already dirty with a scan scheduled or pending; nothing new to do.
                    return;
            }

            _state = RepositoryRefreshState.Dirty;
            _debounceTimer?.Dispose();
            _debounceTimer = new Timer(_ => onDebounceElapsed(), null, debounceMilliseconds, Timeout.Infinite);
        }
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

            _debounceTimer?.Dispose();
            _debounceTimer = null;
            _state = RepositoryRefreshState.Refreshing;
            coalescedTask = null;
            return true;
        }
    }

    /// <summary>Ends the current scan. Returns true when a follow-up scan must run immediately because a dirty
    /// event arrived while this scan was in flight.</summary>
    public bool EndRefresh(GitChangeStatusResult result, out TaskCompletionSource<GitChangeStatusResult>? completionToSignal)
    {
        lock (_gate)
        {
            completionToSignal = _pendingCompletion;
            _pendingCompletion = null;

            if (_state == RepositoryRefreshState.RefreshingAndDirty)
            {
                _state = RepositoryRefreshState.Refreshing;
                return true;
            }

            if (_state != RepositoryRefreshState.Disposed)
            {
                _state = RepositoryRefreshState.Clean;
            }

            return false;
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
