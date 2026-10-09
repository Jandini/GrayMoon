using GrayMoon.Worker.Abstractions;
using GrayMoon.Worker.Jobs;
using Microsoft.Extensions.Logging;

namespace GrayMoon.Worker.Services;

/// <summary>
/// Keeps the sync that <c>git worktree add</c> triggers through the <c>post-checkout</c> hook from competing with the
/// worktree creation itself. The hook fires while the new worktree is still being created, queues a full
/// <c>Checkout</c> sync (fetch, GitVersion, project scan) on the same main queue as <c>CreateGitWorktree</c>, and that
/// sync holds a worker slot for many seconds - so the later creates of a Feature wait behind the earlier worktrees' syncs.
/// </summary>
/// <remarks>
/// A <c>Checkout</c> hook for a worktree that is being created is held back instead of dropped, then released once no
/// create is running and a short quiet period has passed. The sync still happens (the Feature's version and counts are
/// refreshed through the normal SyncCommand path), just not in the middle of Create.
/// </remarks>
public interface IWorktreeCreateHookDeferral
{
    /// <summary>Marks <paramref name="worktreePath"/> as being created until the returned scope is disposed.</summary>
    IDisposable BeginCreate(string worktreePath);

    /// <summary>
    /// Holds back <paramref name="job"/> when it is a <c>Checkout</c> hook for a worktree currently being created.
    /// Returns false (nothing held) for any other job, which the caller enqueues as usual.
    /// </summary>
    bool TryDefer(INotifyJob job);
}

public sealed class WorktreeCreateHookDeferral(IJobQueue jobQueue, ILogger<WorktreeCreateHookDeferral> logger, TimeSpan? quietPeriod = null)
    : IWorktreeCreateHookDeferral, IDisposable
{
    /// <summary>No create running for this long, and the held syncs are released.</summary>
    internal static readonly TimeSpan QuietPeriod = TimeSpan.FromMilliseconds(500);

    private readonly object _gate = new();
    private readonly Dictionary<string, int> _active = new(PathComparer);
    private readonly Dictionary<string, INotifyJob> _deferred = new(PathComparer);
    private readonly TimeSpan _quietPeriod = quietPeriod ?? QuietPeriod;
    private int _activeTotal;
    private bool _disposed;
    private Timer? _timer;

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public IDisposable BeginCreate(string worktreePath)
    {
        var key = Normalize(worktreePath);
        lock (_gate)
        {
            _active[key] = _active.GetValueOrDefault(key) + 1;
            _activeTotal++;
        }

        return new Scope(this, key);
    }

    public bool TryDefer(INotifyJob job)
    {
        if (job.HookKind != NotifyHookKind.Checkout)
            return false;

        var key = Normalize(job.RepositoryPath);
        lock (_gate)
        {
            if (!_active.ContainsKey(key))
                return false;

            // One held sync per worktree is enough: it reads the repository's state when it finally runs.
            _deferred[key] = new NotifySyncJob
            {
                RepositoryId = job.RepositoryId,
                WorkspaceId = job.WorkspaceId,
                RepositoryPath = job.RepositoryPath,
                HookKind = job.HookKind,
                FreshWorktree = true
            };
        }

        logger.LogDebug("Checkout sync held back while its worktree is being created: {RepoPath}", job.RepositoryPath);
        return true;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _timer?.Dispose();
            _timer = null;
        }
    }

    private void EndCreate(string key)
    {
        lock (_gate)
        {
            if (_active.TryGetValue(key, out var count))
            {
                if (count <= 1)
                    _active.Remove(key);
                else
                    _active[key] = count - 1;
            }

            _activeTotal = Math.Max(0, _activeTotal - 1);
            if (_activeTotal == 0 && _deferred.Count > 0 && !_disposed)
            {
                _timer ??= new Timer(_ => _ = FlushAsync());
                _timer.Change(_quietPeriod, Timeout.InfiniteTimeSpan);
            }
        }
    }

    private async Task FlushAsync()
    {
        List<INotifyJob> released;
        lock (_gate)
        {
            // A create started again during the quiet period: the last scope to close re-arms the timer.
            if (_disposed || _activeTotal > 0 || _deferred.Count == 0)
                return;

            released = _deferred.Values.ToList();
            _deferred.Clear();
        }

        logger.LogInformation("Releasing {Count} Checkout sync(s) held back during worktree creation.", released.Count);
        foreach (var job in released)
        {
            try
            {
                await jobQueue.EnqueueAsync(JobEnvelope.Notify(job));
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not enqueue held Checkout sync for {RepoPath}.", job.RepositoryPath);
            }
        }
    }

    private static string Normalize(string path)
    {
        try
        {
            return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception)
        {
            return path.Trim();
        }
    }

    private sealed class Scope(WorktreeCreateHookDeferral owner, string key) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                owner.EndCreate(key);
        }
    }
}
