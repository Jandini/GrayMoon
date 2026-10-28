using GrayMoon.Application.Features;
using GrayMoon.Common.Git;
using Microsoft.Extensions.Options;

namespace GrayMoon.App.Services.GitChanges;

public enum GitChangesAuditReason
{
    /// <summary>The audit interval elapsed.</summary>
    Periodic,

    /// <summary>The Worker connection came back.</summary>
    WorkerOnline,

    /// <summary>An Ensure response carried a different Worker instance id than before, so watchers were rebuilt.</summary>
    WorkerRestarted,

    /// <summary>A workspace became active after nothing was looking at it.</summary>
    Activation,
}

/// <summary>Asks for an authoritative status scan of one workspace context.</summary>
public interface IGitChangesAuditScheduler
{
    /// <summary>Never blocks and never throws; the scan runs in the background, single-flight per context.</summary>
    void Request(int workspaceId, WorkspaceFeatureContextId contextId, GitChangesAuditReason reason);
}

/// <summary>
/// The verify half of Git Changes monitoring. Watchers are an optimisation; this scheduler is what guarantees the
/// persisted projection catches up with the disk even when a watcher was missing, overflowed or silently dead. Every
/// context is audited at most once at a time, requests that arrive while an audit that already saw the current state
/// is running collapse into it, parallelism is bounded across the whole App and every scan has a timeout. It never
/// renews watchers; <see cref="GitChangesWatcherKeeper"/> does that without git.
/// </summary>
public sealed class GitChangesAuditScheduler(
    IGitChangesWorkspaceScanner scanner,
    IWorkspaceGitChangesActivityTracker activityTracker,
    IWorkspaceGitChangesMonitoringPause monitoringPause,
    WorkerConnectionTracker connectionTracker,
    IServiceScopeFactory scopeFactory,
    IOptions<GitChangesOptions> options,
    TimeProvider timeProvider,
    ILogger<GitChangesAuditScheduler> logger) : BackgroundService, IGitChangesAuditScheduler
{
    /// <summary>A request this soon after an audit started is answered by that audit.</summary>
    internal static readonly TimeSpan CoalesceWindow = TimeSpan.FromSeconds(5);

    /// <summary>Gap between the audits of consecutive contexts in one periodic pass, so they do not start together.</summary>
    internal static readonly TimeSpan PeriodicStagger = TimeSpan.FromSeconds(2);

    private readonly object _gate = new();
    private readonly Dictionary<(int WorkspaceId, int ContextId), ContextState> _states = [];
    private readonly List<Task> _running = [];
    private readonly CancellationTokenSource _stopping = new();
    private readonly SemaphoreSlim _parallel = new(Math.Max(1, options.Value.WatcherAuditMaxParallelContexts));

    public void Request(int workspaceId, WorkspaceFeatureContextId contextId, GitChangesAuditReason reason)
    {
        if (_stopping.IsCancellationRequested)
        {
            return;
        }

        var key = (workspaceId, contextId.Value);
        var now = timeProvider.GetUtcNow();
        Task? runner = null;
        lock (_gate)
        {
            if (!_states.TryGetValue(key, out var state))
            {
                state = new ContextState();
                _states[key] = state;
            }

            if (state.Running)
            {
                if (now - state.StartedAt < CoalesceWindow)
                {
                    logger.LogDebug(
                        "[GCDiag] audit request collapsed into the running audit: workspace={WorkspaceId} context={ContextId} reason={Reason}",
                        workspaceId, contextId.Value, reason);
                }
                else
                {
                    state.Pending = true;
                    state.PendingReason = reason;
                }

                return;
            }

            if (state.StartedAt != default && now - state.StartedAt < CoalesceWindow)
            {
                logger.LogDebug(
                    "[GCDiag] audit request answered by the audit that just started: workspace={WorkspaceId} context={ContextId} reason={Reason}",
                    workspaceId, contextId.Value, reason);
                return;
            }

            state.Running = true;
            state.StartedAt = now;
            runner = Task.Run(() => DrainAsync(workspaceId, contextId, state, reason));
            _running.Add(runner);
        }

        _ = runner.ContinueWith(
            t =>
            {
                lock (_gate)
                {
                    _running.Remove(t);
                }
            },
            TaskScheduler.Default);
    }

    /// <summary>Completes when no audit is running or queued. Used by tests and by shutdown.</summary>
    internal async Task WhenIdleAsync()
    {
        while (true)
        {
            Task[] snapshot;
            lock (_gate)
            {
                snapshot = [.. _running];
            }

            if (snapshot.Length == 0)
            {
                return;
            }

            await Task.WhenAll(snapshot);
            await Task.Yield();
        }
    }

    /// <summary>When the last audit of this context finished, or null if none has.</summary>
    internal DateTimeOffset? LastCompletedAt(int workspaceId, WorkspaceFeatureContextId contextId)
    {
        lock (_gate)
        {
            return _states.TryGetValue((workspaceId, contextId.Value), out var state) ? state.CompletedAt : null;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromMinutes(Math.Max(1, options.Value.WatcherAuditIntervalMinutes));
        using var timer = new PeriodicTimer(interval, timeProvider);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await RunPeriodicPassAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "[GCDiag] periodic audit pass failed");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await _stopping.CancelAsync();
        await base.StopAsync(cancellationToken);
        await Task.WhenAny(WhenIdleAsync(), Task.Delay(TimeSpan.FromSeconds(5), cancellationToken));
    }

    internal async Task RunPeriodicPassAsync(CancellationToken cancellationToken)
    {
        if (connectionTracker.State != WorkerConnectionState.Online)
        {
            logger.LogDebug("[GCDiag] periodic audit skipped: Worker is not online");
            return;
        }

        var activeWorkspaceIds = activityTracker.GetActiveWorkspaceIds();
        if (activeWorkspaceIds.Count == 0)
        {
            logger.LogDebug("[GCDiag] periodic audit skipped: no active workspaces");
            return;
        }

        var requested = 0;
        foreach (var workspaceId in activeWorkspaceIds)
        {
            IReadOnlyList<WorkspaceFeatureContextInfo> contexts;
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                contexts = await scope.ServiceProvider.GetRequiredService<IWorkspaceFeatureContextResolver>()
                    .ListForWorkspaceAsync(workspaceId, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "[GCDiag] periodic audit could not list contexts: workspace={WorkspaceId}", workspaceId);
                continue;
            }

            foreach (var context in contexts)
            {
                if (monitoringPause.IsPaused(context.ContextId.Value))
                {
                    continue;
                }

                if (requested++ > 0)
                {
                    await Task.Delay(PeriodicStagger, timeProvider, cancellationToken);
                }

                Request(workspaceId, context.ContextId, GitChangesAuditReason.Periodic);
            }
        }
    }

    private async Task DrainAsync(int workspaceId, WorkspaceFeatureContextId contextId, ContextState state, GitChangesAuditReason reason)
    {
        while (true)
        {
            try
            {
                await _parallel.WaitAsync(_stopping.Token);
                try
                {
                    await RunOnceAsync(workspaceId, contextId, reason);
                }
                finally
                {
                    _parallel.Release();
                }
            }
            catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "[GCDiag] audit failed unexpectedly: workspace={WorkspaceId} context={ContextId}", workspaceId, contextId.Value);
            }

            lock (_gate)
            {
                state.CompletedAt = timeProvider.GetUtcNow();
                if (!state.Pending || _stopping.IsCancellationRequested)
                {
                    state.Pending = false;
                    state.Running = false;
                    return;
                }

                state.Pending = false;
                state.StartedAt = timeProvider.GetUtcNow();
                reason = state.PendingReason;
            }
        }
    }

    private async Task RunOnceAsync(int workspaceId, WorkspaceFeatureContextId contextId, GitChangesAuditReason reason)
    {
        if (!activityTracker.IsActive(workspaceId))
        {
            logger.LogDebug("[GCDiag] audit skipped, workspace no longer active: workspace={WorkspaceId} context={ContextId}", workspaceId, contextId.Value);
            return;
        }

        if (monitoringPause.IsPaused(contextId.Value))
        {
            logger.LogDebug("[GCDiag] audit skipped, context paused for removal: workspace={WorkspaceId} context={ContextId}", workspaceId, contextId.Value);
            return;
        }

        if (connectionTracker.State != WorkerConnectionState.Online)
        {
            logger.LogDebug("[GCDiag] audit skipped, Worker is not online: workspace={WorkspaceId} context={ContextId}", workspaceId, contextId.Value);
            return;
        }

        var started = timeProvider.GetTimestamp();
        logger.LogInformation(
            "[GCDiag] audit start: workspace={WorkspaceId} context={ContextId} reason={Reason}", workspaceId, contextId.Value, reason);

        using var timeout = new CancellationTokenSource(
            TimeSpan.FromSeconds(Math.Max(1, options.Value.WatcherAuditTimeoutSeconds)), timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, _stopping.Token);
        try
        {
            await scanner.ScanWorkspaceAsync(workspaceId, contextId, linked.Token);
            logger.LogInformation(
                "[GCDiag] audit end: workspace={WorkspaceId} context={ContextId} elapsedMs={ElapsedMs}",
                workspaceId, contextId.Value, (long)timeProvider.GetElapsedTime(started).TotalMilliseconds);
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
        {
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning(
                "[GCDiag] audit timed out: workspace={WorkspaceId} context={ContextId} afterSec={Seconds}",
                workspaceId, contextId.Value, (long)timeProvider.GetElapsedTime(started).TotalSeconds);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[GCDiag] audit failed: workspace={WorkspaceId} context={ContextId}", workspaceId, contextId.Value);
        }
    }

    public override void Dispose()
    {
        _stopping.Dispose();
        _parallel.Dispose();
        base.Dispose();
    }

    private sealed class ContextState
    {
        public bool Running;
        public bool Pending;
        public GitChangesAuditReason PendingReason;
        public DateTimeOffset StartedAt;
        public DateTimeOffset? CompletedAt;
    }
}
