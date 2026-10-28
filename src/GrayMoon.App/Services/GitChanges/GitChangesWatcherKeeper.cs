using System.Collections.Concurrent;
using GrayMoon.Application.Features;
using GrayMoon.Common.Git;
using Microsoft.Extensions.Options;

namespace GrayMoon.App.Services.GitChanges;

public enum GitChangesKeeperTickReason
{
    /// <summary>The renewal timer fired.</summary>
    Scheduled,

    /// <summary>The Worker connection came back.</summary>
    WorkerOnline,
}

/// <summary>
/// The keepalive half of Git Changes monitoring. On a fixed cadence it asks the Worker to renew (or create, or
/// restart) the file watchers of every context of every active workspace, one Git-free
/// <c>EnsureGitChangesWatchers</c> batch per context. It never runs git and never waits for a scan, so a slow or
/// hung status scan can no longer cause a watcher to expire. Each call has its own timeout and its own error
/// boundary, so one stuck context cannot delay another. After a Worker reconnect it renews first and then asks the
/// <see cref="IGitChangesAuditScheduler"/> for an authoritative scan, because changes made while nothing was watching
/// are only recoverable by scanning.
/// </summary>
public sealed class GitChangesWatcherKeeper(
    IGitChangesContextEnsurer ensurer,
    IGitChangesAuditScheduler auditScheduler,
    IWorkspaceGitChangesActivityTracker activityTracker,
    IWorkspaceGitChangesMonitoringPause monitoringPause,
    WorkerConnectionTracker connectionTracker,
    IServiceScopeFactory scopeFactory,
    IOptions<GitChangesOptions> options,
    TimeProvider timeProvider,
    ILogger<GitChangesWatcherKeeper> logger) : BackgroundService
{
    /// <summary>A reconnect that follows another reconnect this closely is the same reconnect.</summary>
    internal static readonly TimeSpan ReconnectCoalesceWindow = TimeSpan.FromSeconds(5);

    private readonly ConcurrentDictionary<(int WorkspaceId, int ContextId), string> _seenWorkerInstance = new();
    private readonly object _reconnectGate = new();
    private int _lastState = -1;
    private bool _reconnectRunning;
    private DateTimeOffset _lastReconnectAt;
    private Task _reconnectTask = Task.CompletedTask;
    private int _warnedGraceSeconds;
    private CancellationToken _stoppingToken;

    private TimeSpan RenewalInterval
    {
        get
        {
            var o = options.Value;
            return TimeSpan.FromMinutes(Math.Clamp(o.WatcherRenewalIntervalMinutes, 1, Math.Max(1, o.WatcherIdleGraceMinutes - 1)));
        }
    }

    private TimeSpan EnsureTimeout
    {
        get
        {
            var configured = TimeSpan.FromSeconds(Math.Max(1, options.Value.WatcherEnsureTimeoutSeconds));
            // A call must always finish well before the next tick, or ticks would queue behind it.
            var half = RenewalInterval / 2;
            return configured < half ? configured : half;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _stoppingToken = stoppingToken;
        Action<WorkerConnectionState> handler = HandleWorkerState;
        connectionTracker.OnStateChanged(handler);
        try
        {
            var interval = RenewalInterval;
            using var timer = new PeriodicTimer(interval, timeProvider);
            var planned = timeProvider.GetUtcNow() + interval;
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                // Lateness is measured against this tick only and the anchor then moves to now, so one long suspend
                // (sleep and resume) is reported once instead of on every later tick.
                var tickPlanned = planned;
                planned = timeProvider.GetUtcNow() + interval;
                try
                {
                    await RunTickAsync(GitChangesKeeperTickReason.Scheduled, tickPlanned, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "[GCDiag] keeper tick failed");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        finally
        {
            connectionTracker.RemoveStateChanged(handler);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        Task reconnect;
        lock (_reconnectGate)
        {
            reconnect = _reconnectTask;
        }

        await Task.WhenAny(reconnect, Task.Delay(TimeSpan.FromSeconds(5), cancellationToken));
    }

    /// <summary>
    /// Worker connection state callback. An <c>Online</c> that follows an <c>Online</c>, or that arrives right after a
    /// reconnect run started, is ignored, so one reconnect produces exactly one Ensure and one audit per context.
    /// </summary>
    internal void HandleWorkerState(WorkerConnectionState state)
    {
        logger.LogInformation("[GCDiag] Worker connection state changed: {State}", state);
        var previous = Interlocked.Exchange(ref _lastState, (int)state);
        if (state != WorkerConnectionState.Online || previous == (int)WorkerConnectionState.Online)
        {
            return;
        }

        var now = timeProvider.GetUtcNow();
        lock (_reconnectGate)
        {
            if (_reconnectRunning || now - _lastReconnectAt < ReconnectCoalesceWindow)
            {
                logger.LogDebug("[GCDiag] keeper reconnect run skipped: one is already running or just ran");
                return;
            }

            _reconnectRunning = true;
            _lastReconnectAt = now;
            _reconnectTask = Task.Run(async () =>
            {
                try
                {
                    await RunTickAsync(GitChangesKeeperTickReason.WorkerOnline, plannedAt: null, _stoppingToken);
                }
                catch (OperationCanceledException) when (_stoppingToken.IsCancellationRequested)
                {
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "[GCDiag] keeper reconnect run failed");
                }
                finally
                {
                    lock (_reconnectGate)
                    {
                        _reconnectRunning = false;
                    }
                }
            });
        }
    }

    /// <summary>Completes when the reconnect run, if any, has finished. Used by tests.</summary>
    internal Task WhenReconnectIdleAsync()
    {
        lock (_reconnectGate)
        {
            return _reconnectTask;
        }
    }

    internal async Task RunTickAsync(GitChangesKeeperTickReason reason, DateTimeOffset? plannedAt, CancellationToken cancellationToken)
    {
        var started = timeProvider.GetTimestamp();
        var late = plannedAt is { } p ? timeProvider.GetUtcNow() - p : (TimeSpan?)null;
        if (late is { } l && l > TimeSpan.FromSeconds(30))
        {
            logger.LogWarning("[GCDiag] keeper tick late: lateSec={LateSec}", (long)l.TotalSeconds);
        }

        if (connectionTracker.State != WorkerConnectionState.Online)
        {
            logger.LogInformation("[GCDiag] keeper tick skipped: Worker is not online reason={Reason}", reason);
            return;
        }

        var activeWorkspaceIds = activityTracker.GetActiveWorkspaceIds();
        if (activeWorkspaceIds.Count == 0)
        {
            logger.LogDebug("[GCDiag] keeper tick skipped: no active workspaces reason={Reason}", reason);
            return;
        }

        var targets = new List<(int WorkspaceId, WorkspaceFeatureContextId ContextId)>();
        foreach (var workspaceId in activeWorkspaceIds)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var contexts = await scope.ServiceProvider.GetRequiredService<IWorkspaceFeatureContextResolver>()
                    .ListForWorkspaceAsync(workspaceId, cancellationToken);
                targets.AddRange(contexts.Select(c => (workspaceId, c.ContextId)));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "[GCDiag] keeper could not list contexts: workspace={WorkspaceId}", workspaceId);
            }
        }

        using var gate = new SemaphoreSlim(Math.Max(1, options.Value.WatcherKeeperMaxParallelContexts));
        await Task.WhenAll(targets.Select(t => EnsureContextAsync(t.WorkspaceId, t.ContextId, reason, gate, cancellationToken)));

        logger.LogDebug(
            "[GCDiag] keeper tick done: reason={Reason} workspaces={Workspaces} contexts={Contexts} lateMs={LateMs} elapsedMs={ElapsedMs}",
            reason, activeWorkspaceIds.Count, targets.Count, (long)(late?.TotalMilliseconds ?? 0),
            (long)timeProvider.GetElapsedTime(started).TotalMilliseconds);
    }

    private async Task EnsureContextAsync(
        int workspaceId, WorkspaceFeatureContextId contextId, GitChangesKeeperTickReason reason, SemaphoreSlim gate, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            // Remove Feature owns a paused context; renewing there would re-create a watcher on a folder being deleted.
            if (monitoringPause.IsPaused(contextId.Value))
            {
                logger.LogDebug("[GCDiag] keeper context skipped (paused for removal): workspace={WorkspaceId} context={ContextId}", workspaceId, contextId.Value);
                return;
            }

            var started = timeProvider.GetTimestamp();
            using var timeout = new CancellationTokenSource(EnsureTimeout, timeProvider);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, cancellationToken);
            GitChangesEnsureResult result;
            try
            {
                result = await ensurer.EnsureAsync(workspaceId, contextId, linked.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning("[GCDiag] ensure watchers timed out: workspace={WorkspaceId} context={ContextId}", workspaceId, contextId.Value);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "[GCDiag] ensure watchers threw: workspace={WorkspaceId} context={ContextId}", workspaceId, contextId.Value);
                return;
            }

            if (!result.Success)
            {
                logger.LogWarning(
                    "[GCDiag] ensure watchers failed: workspace={WorkspaceId} context={ContextId} {Error}",
                    workspaceId, contextId.Value, result.ErrorMessage);
                return;
            }

            var counts = string.Join(' ', result.Results.GroupBy(r => r.Status).OrderBy(g => g.Key).Select(g => $"{g.Key}={g.Count()}"));
            logger.Log(
                result.Results.Any(r => r.Status is "Degraded" or "Created" or "Recovered") ? LogLevel.Information : LogLevel.Debug,
                "[GCDiag] ensure watchers: workspace={WorkspaceId} context={ContextId} repos={Total} {Counts} worker={Worker} graceSec={Grace} elapsedMs={ElapsedMs}",
                workspaceId, contextId.Value, result.Results.Count, counts, result.WorkerInstanceId, result.EffectiveGraceSeconds,
                (long)timeProvider.GetElapsedTime(started).TotalMilliseconds);

            CheckCadence(result.EffectiveGraceSeconds);

            var restarted = false;
            if (!string.IsNullOrEmpty(result.WorkerInstanceId))
            {
                var key = (workspaceId, contextId.Value);
                restarted = _seenWorkerInstance.TryGetValue(key, out var previous) && previous != result.WorkerInstanceId;
                _seenWorkerInstance[key] = result.WorkerInstanceId;
            }

            // Created and Recovered watchers queue their own refresh on the Worker, so only a reconnect or a changed
            // Worker instance (which can happen without any Online event) needs an authoritative scan.
            if (reason == GitChangesKeeperTickReason.WorkerOnline)
            {
                auditScheduler.Request(workspaceId, contextId, GitChangesAuditReason.WorkerOnline);
            }
            else if (restarted)
            {
                logger.LogInformation(
                    "[GCDiag] Worker instance changed: workspace={WorkspaceId} context={ContextId} worker={Worker}",
                    workspaceId, contextId.Value, result.WorkerInstanceId);
                auditScheduler.Request(workspaceId, contextId, GitChangesAuditReason.WorkerRestarted);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Warns once when the cadence would leave fewer than two renewal attempts inside the Worker's idle grace.</summary>
    private void CheckCadence(int graceSeconds)
    {
        if (graceSeconds <= 0 || graceSeconds == _warnedGraceSeconds)
        {
            return;
        }

        var interval = RenewalInterval.TotalSeconds;
        if (graceSeconds < 2 * interval)
        {
            _warnedGraceSeconds = graceSeconds;
            logger.LogWarning(
                "[GCDiag] keeper cadence too slow: renewal every {IntervalSec}s leaves fewer than two attempts inside the Worker idle grace of {GraceSec}s",
                (long)interval, graceSeconds);
        }
    }
}
