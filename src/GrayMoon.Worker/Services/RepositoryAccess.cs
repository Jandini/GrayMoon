using System.Diagnostics;
using GrayMoon.Worker.Abstractions;
using Microsoft.Extensions.Logging;

namespace GrayMoon.Worker.Services;

/// <inheritdoc cref="IRepositoryAccess"/>
public sealed class RepositoryAccess(ILogger<RepositoryAccess> logger) : IRepositoryAccess
{
    private readonly object _lock = new();
    private readonly List<Claim> _claims = [];
    private readonly List<SharedLease> _leases = [];
    private readonly List<ReleasableEntry> _releasables = [];
    private readonly List<ObserverEntry> _observers = [];
    private readonly List<ExclusiveScope> _scopes = [];
    private TaskCompletionSource _changed = NewSignal();

    public IRepositoryAccessLease? TryAcquireShared(
        string path,
        RepositoryAccessKind kind,
        Action? forceTerminate = null,
        IRepositoryExclusiveScope? owner = null)
    {
        var key = RepositoryPathKey.Normalize(path);
        lock (_lock)
        {
            var covering = _claims.Where(c => RepositoryPathKey.IsUnderOrEqual(key, c.Path)).ToList();
            if (covering.Any(c => !ReferenceEquals(c.Scope, owner)))
            {
                return null;
            }

            // Work inside the remover's own claim is not something the removal waits for or cancels.
            var tracked = covering.Count == 0;
            var lease = new SharedLease(this, key, kind, forceTerminate, tracked, new CancellationTokenSource());
            if (tracked)
            {
                _leases.Add(lease);
            }

            return lease;
        }
    }

    public async Task<IRepositoryExclusiveScope> AcquireExclusiveAsync(
        IReadOnlyCollection<string> paths,
        CancellationToken cancellationToken,
        RepositoryAccessTimings? timings = null)
    {
        var t = timings ?? RepositoryAccessTimings.Default;
        var keys = paths
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(RepositoryPathKey.Normalize)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (keys.Count == 0)
        {
            throw new ArgumentException("At least one path is required.", nameof(paths));
        }

        var scope = new ExclusiveScope(this, keys);
        List<ReleasableEntry> toRelease;
        List<ObserverEntry> observers;
        lock (_lock)
        {
            _scopes.Add(scope);
            foreach (var key in keys)
            {
                _claims.Add(new Claim(key, scope));
            }

            toRelease = _releasables.Where(r => keys.Any(k => RepositoryPathKey.IsUnderOrEqual(r.Path, k))).ToList();
            _releasables.RemoveAll(toRelease.Contains);
            observers = _observers.Where(o => keys.Any(k => RepositoryPathKey.IsStrictAncestor(o.Path, k))).ToList();
            foreach (var observer in observers)
            {
                scope.Observers.Add(observer);
            }
        }

        try
        {
            foreach (var observer in observers)
            {
                Safe(observer.Observer.ClaimBeneathStarted, "observer start", observer.Path);
            }

            foreach (var releasable in toRelease)
            {
                Safe(releasable.Releasable.Release, "release", releasable.Path);
            }

            // Readers go at once; they only look, so there is nothing to protect.
            CancelLeases(keys, readOnlyOnly: true);

            if (!await WaitForDrainAsync(keys, t.MutatingGrace, cancellationToken))
            {
                CancelLeases(keys, readOnlyOnly: false);
                var left = t.Ceiling > t.MutatingGrace ? t.Ceiling - t.MutatingGrace : TimeSpan.Zero;
                if (!await WaitForDrainAsync(keys, left, cancellationToken))
                {
                    TerminateRemaining(keys);
                    if (!await WaitForDrainAsync(keys, t.ForceSettle, cancellationToken))
                    {
                        throw new RepositoryAccessException(
                            $"Could not release every handle the Worker holds on {string.Join("; ", keys)}.");
                    }
                }
            }

            return scope;
        }
        catch
        {
            scope.Dispose();
            throw;
        }
    }

    public IDisposable RegisterReleasable(string path, IReleasable releasable)
    {
        var key = RepositoryPathKey.Normalize(path);
        ReleasableEntry entry;
        bool covered;
        lock (_lock)
        {
            entry = new ReleasableEntry(key, releasable);
            covered = _claims.Any(c => RepositoryPathKey.IsUnderOrEqual(key, c.Path));
            if (!covered)
            {
                _releasables.Add(entry);
            }
        }

        if (covered)
        {
            // Registered into a folder that is already being removed: let go immediately.
            Safe(releasable.Release, "release", key);
            return NoopDisposable.Instance;
        }

        return new Unregister(() =>
        {
            lock (_lock)
            {
                _releasables.Remove(entry);
            }
        });
    }

    public IDisposable RegisterAncestorObserver(string path, IRepositoryAccessObserver observer)
    {
        var key = RepositoryPathKey.Normalize(path);
        var entry = new ObserverEntry(key, observer);
        List<ExclusiveScope> activeBeneath;
        lock (_lock)
        {
            _observers.Add(entry);
            activeBeneath = _scopes
                .Where(s => s.Keys.Any(k => RepositoryPathKey.IsStrictAncestor(key, k)))
                .ToList();
            foreach (var scope in activeBeneath)
            {
                scope.Observers.Add(entry);
            }
        }

        foreach (var _ in activeBeneath)
        {
            Safe(observer.ClaimBeneathStarted, "observer start", key);
        }

        return new Unregister(() =>
        {
            lock (_lock)
            {
                _observers.Remove(entry);
                foreach (var scope in _scopes)
                {
                    scope.Observers.Remove(entry);
                }
            }
        });
    }

    public bool IsUnderRemoval(string path)
    {
        var key = RepositoryPathKey.Normalize(path);
        lock (_lock)
        {
            return _claims.Any(c => RepositoryPathKey.IsUnderOrEqual(key, c.Path));
        }
    }

    private void CancelLeases(IReadOnlyCollection<string> keys, bool readOnlyOnly)
    {
        List<SharedLease> targets;
        lock (_lock)
        {
            targets = _leases
                .Where(l => keys.Any(k => RepositoryPathKey.IsUnderOrEqual(l.Path, k))
                            && (!readOnlyOnly || l.Kind == RepositoryAccessKind.ReadOnly))
                .ToList();
        }

        foreach (var lease in targets)
        {
            lease.CancelYield();
        }
    }

    private void TerminateRemaining(IReadOnlyCollection<string> keys)
    {
        List<SharedLease> targets;
        lock (_lock)
        {
            targets = _leases.Where(l => keys.Any(k => RepositoryPathKey.IsUnderOrEqual(l.Path, k))).ToList();
        }

        foreach (var lease in targets)
        {
            logger.LogWarning(
                "Terminating a Worker-started process that did not release {Path} ({Kind}) when its Feature was removed.",
                lease.Path, lease.Kind);
            Safe(() => lease.ForceTerminate?.Invoke(), "force terminate", lease.Path);
        }
    }

    private async Task<bool> WaitForDrainAsync(IReadOnlyCollection<string> keys, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        while (true)
        {
            Task changed;
            lock (_lock)
            {
                if (!_leases.Any(l => keys.Any(k => RepositoryPathKey.IsUnderOrEqual(l.Path, k))))
                {
                    return true;
                }

                changed = _changed.Task;
            }

            var remaining = timeout - Stopwatch.GetElapsedTime(started);
            if (remaining <= TimeSpan.Zero)
            {
                return false;
            }

            using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            await Task.WhenAny(changed, Task.Delay(remaining, delayCts.Token));
            delayCts.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    private void Safe(Action action, string what, string path)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Repository access: {What} failed for {Path}", what, path);
        }
    }

    // Caller holds _lock.
    private void SignalChanged()
    {
        var previous = _changed;
        _changed = NewSignal();
        previous.TrySetResult();
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed record Claim(string Path, ExclusiveScope Scope);

    private sealed record ReleasableEntry(string Path, IReleasable Releasable);

    private sealed record ObserverEntry(string Path, IRepositoryAccessObserver Observer);

    private sealed class Unregister(Action action) : IDisposable
    {
        private int _done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0)
            {
                action();
            }
        }
    }

    private sealed class NoopDisposable : IDisposable
    {
        public static readonly NoopDisposable Instance = new();

        public void Dispose()
        {
        }
    }

    private sealed class SharedLease(RepositoryAccess owner, string path, RepositoryAccessKind kind, Action? forceTerminate, bool tracked, CancellationTokenSource cts)
        : IRepositoryAccessLease
    {
        private readonly CancellationTokenSource _cts = cts;
        private int _disposed;

        public string Path { get; } = path;

        public RepositoryAccessKind Kind { get; } = kind;

        public Action? ForceTerminate { get; } = forceTerminate;

        // Captured up front: a token stays readable after its source is disposed, the source itself does not.
        public CancellationToken Yield { get; } = cts.Token;

        public void CancelYield()
        {
            try
            {
                _cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Released concurrently; nothing left to cancel.
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            if (tracked)
            {
                lock (owner._lock)
                {
                    owner._leases.Remove(this);
                    owner.SignalChanged();
                }
            }

            _cts.Dispose();
        }
    }

    private sealed class ExclusiveScope(RepositoryAccess owner, IReadOnlyList<string> keys) : IRepositoryExclusiveScope
    {
        private int _disposed;

        public IReadOnlyList<string> Keys { get; } = keys;

        public HashSet<ObserverEntry> Observers { get; } = [];

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            List<ObserverEntry> toNotify;
            lock (owner._lock)
            {
                owner._claims.RemoveAll(c => ReferenceEquals(c.Scope, this));
                owner._scopes.Remove(this);
                toNotify = [.. Observers];
                Observers.Clear();
            }

            foreach (var observer in toNotify)
            {
                owner.Safe(observer.Observer.ClaimBeneathEnded, "observer end", observer.Path);
            }
        }
    }
}

/// <summary>Path comparison shared by the access broker: full path, no trailing separator, ordinal ignore-case (Windows).</summary>
internal static class RepositoryPathKey
{
    public static string Normalize(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    /// <summary>True when <paramref name="path"/> equals <paramref name="root"/> or lies beneath it. Both already normalized.</summary>
    public static bool IsUnderOrEqual(string path, string root) =>
        string.Equals(path, root, StringComparison.OrdinalIgnoreCase) || IsStrictlyUnder(path, root);

    /// <summary>True when <paramref name="ancestor"/> is a proper ancestor of <paramref name="path"/>.</summary>
    public static bool IsStrictAncestor(string ancestor, string path) => IsStrictlyUnder(path, ancestor);

    private static bool IsStrictlyUnder(string path, string root) =>
        path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}
