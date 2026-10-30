using GrayMoon.App.Services.GitChanges;
using GrayMoon.Application.Features;
using Microsoft.Extensions.DependencyInjection;

namespace GrayMoon.App.Tests;

/// <summary>A clock that only moves when a test says so, including the timers built on it.</summary>
internal sealed class ManualClock : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<ManualTimer> _timers = [];
    private DateTimeOffset _now = new(2026, 10, 28, 9, 0, 0, TimeSpan.Zero);

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
        {
            return _now;
        }
    }

    public override long GetTimestamp() => GetUtcNow().UtcTicks;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        lock (_gate)
        {
            _timers.Add(timer);
        }

        timer.Change(dueTime, period);
        return timer;
    }

    /// <summary>Time until the soonest armed timer, or null when nothing is waiting.</summary>
    public TimeSpan? NextTimerDelay
    {
        get
        {
            lock (_gate)
            {
                DateTimeOffset? next = null;
                foreach (var timer in _timers)
                {
                    if (timer.Due is { } due && (next is null || due < next))
                    {
                        next = due;
                    }
                }

                return next is { } dueAt ? dueAt - _now : null;
            }
        }
    }

    /// <summary>Moves time forward, firing every timer that comes due on the way in order.</summary>
    public void Advance(TimeSpan by)
    {
        DateTimeOffset target;
        lock (_gate)
        {
            target = _now + by;
        }

        while (true)
        {
            ManualTimer? next = null;
            lock (_gate)
            {
                foreach (var timer in _timers)
                {
                    if (timer.Due is { } due && due <= target && (next is null || due < next.Due))
                    {
                        next = timer;
                    }
                }

                if (next is null)
                {
                    _now = target;
                    return;
                }

                _now = next.Due!.Value > _now ? next.Due.Value : _now;
                next.Due = next.Period is { } p && p > TimeSpan.Zero ? _now + p : null;
            }

            next.Fire();
        }
    }

    /// <summary>Jumps straight to the new time, as after sleep and resume, and fires each due timer once.</summary>
    public void Jump(TimeSpan by)
    {
        List<ManualTimer> due;
        lock (_gate)
        {
            _now += by;
            due = [.. _timers.Where(t => t.Due is { } d && d <= _now)];
            foreach (var timer in due)
            {
                timer.Due = timer.Period is { } p && p > TimeSpan.Zero ? _now + p : null;
            }
        }

        foreach (var timer in due)
        {
            timer.Fire();
        }
    }

    private void Remove(ManualTimer timer)
    {
        lock (_gate)
        {
            _timers.Remove(timer);
        }
    }

    private sealed class ManualTimer(ManualClock owner, TimerCallback callback, object? state) : ITimer
    {
        public DateTimeOffset? Due;
        public TimeSpan? Period;

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner._gate)
            {
                Due = dueTime == Timeout.InfiniteTimeSpan ? null : owner._now + dueTime;
                Period = period == Timeout.InfiniteTimeSpan ? null : period;
            }

            return true;
        }

        public void Fire() => callback(state);

        public void Dispose() => owner.Remove(this);

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

internal sealed class RecordingAuditScheduler : IGitChangesAuditScheduler
{
    private readonly List<(int WorkspaceId, int ContextId, GitChangesAuditReason Reason)> _requests = [];

    public IReadOnlyList<(int WorkspaceId, int ContextId, GitChangesAuditReason Reason)> Requests
    {
        get
        {
            lock (_requests)
            {
                return [.. _requests];
            }
        }
    }

    public void Request(int workspaceId, WorkspaceFeatureContextId contextId, GitChangesAuditReason reason)
    {
        lock (_requests)
        {
            _requests.Add((workspaceId, contextId.Value, reason));
        }
    }

    public void Clear()
    {
        lock (_requests)
        {
            _requests.Clear();
        }
    }
}

/// <summary>Serves a fixed list of contexts per workspace through the scoped context resolver.</summary>
internal sealed class FakeContextCatalog : IServiceScopeFactory
{
    private readonly Dictionary<int, List<WorkspaceFeatureContextInfo>> _contexts = [];

    /// <summary>Adds a context; the first one added to a workspace is its special Workspace context.</summary>
    public FakeContextCatalog With(int workspaceId, int contextId)
    {
        if (!_contexts.TryGetValue(workspaceId, out var list))
        {
            list = [];
            _contexts[workspaceId] = list;
        }

        list.Add(new WorkspaceFeatureContextInfo
        {
            ContextId = new WorkspaceFeatureContextId(contextId),
            WorkspaceId = workspaceId,
            IsSpecialWorkspace = list.Count == 0,
        });
        return this;
    }

    public IServiceScope CreateScope() => new Scope(this);

    private sealed class Scope(FakeContextCatalog owner) : IServiceScope
    {
        public IServiceProvider ServiceProvider { get; } = new Provider(owner);

        public void Dispose()
        {
        }
    }

    private sealed class Provider(FakeContextCatalog owner) : IServiceProvider
    {
        public object? GetService(Type serviceType) =>
            serviceType == typeof(IWorkspaceFeatureContextResolver) ? new Resolver(owner) : null;
    }

    private sealed class Resolver(FakeContextCatalog owner) : IWorkspaceFeatureContextResolver
    {
        public Task<WorkspaceFeatureContextId> GetOrCreateSpecialWorkspaceContextIdAsync(
            int workspaceId, CancellationToken cancellationToken = default) =>
            Task.FromResult(owner._contexts[workspaceId][0].ContextId);

        public Task<WorkspaceFeatureContextInfo> GetRequiredAsync(
            WorkspaceFeatureContextId contextId, int? expectedWorkspaceId = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(owner._contexts.Values.SelectMany(c => c).Single(c => c.ContextId == contextId));

        public Task<IReadOnlyList<WorkspaceFeatureContextInfo>> ListForWorkspaceAsync(
            int workspaceId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<WorkspaceFeatureContextInfo>>(
                owner._contexts.TryGetValue(workspaceId, out var list) ? [.. list] : []);
    }
}

/// <summary>Collects formatted log messages so a test can assert on warnings.</summary>
internal sealed class CapturingLogger<T> : Microsoft.Extensions.Logging.ILogger<T>
{
    private readonly List<(Microsoft.Extensions.Logging.LogLevel Level, string Message)> _entries = [];

    public IReadOnlyList<(Microsoft.Extensions.Logging.LogLevel Level, string Message)> Entries
    {
        get
        {
            lock (_entries)
            {
                return [.. _entries];
            }
        }
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

    public void Log<TState>(
        Microsoft.Extensions.Logging.LogLevel logLevel,
        Microsoft.Extensions.Logging.EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        lock (_entries)
        {
            _entries.Add((logLevel, formatter(state, exception)));
        }
    }
}
