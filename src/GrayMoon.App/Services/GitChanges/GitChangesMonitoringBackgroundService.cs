using System.Diagnostics;
using GrayMoon.App.Data;
using GrayMoon.Common.Git;
using GrayMoon.Application.Features;
using Microsoft.Extensions.Options;

namespace GrayMoon.App.Services.GitChanges;

/// <summary>
/// Owns the Git Changes background monitoring policy. Per the feature's design, a repository's
/// Worker-side <c>FileSystemWatcher</c> lease belongs to the workspace background service, not the
/// browser page - navigating among workspace pages must never directly start or stop monitoring.
/// This sweep periodically calls <c>GetGitChangeStatus</c> for every repository in every
/// <i>active</i> workspace (per <see cref="IWorkspaceGitChangesActivityTracker"/>) - not every
/// workspace in the database - which both seeds/renews the Worker's <c>GitRepositoryWatcherManager</c>
/// lease (idle grace period is <see cref="GitChangesOptions.WatcherIdleGraceMinutes"/>) and keeps the
/// persisted SQLite projection fresh while a workspace is in view anywhere in GrayMoon. Workspaces with
/// no recent viewer fall out of scope on their own once
/// <see cref="GitChangesOptions.WorkspaceActivityGraceMinutes"/> elapses, so this never blasts every
/// repository across every workspace regardless of whether anyone is looking. The actual per-workspace
/// scan (also used for on-entry warm-up and manual Refresh) lives in
/// <see cref="IGitChangesWorkspaceScanner"/>.
/// </summary>
public sealed class GitChangesMonitoringBackgroundService(
    IServiceScopeFactory scopeFactory,
    IGitChangesWorkspaceScanner scanner,
    IWorkspaceGitChangesActivityTracker activityTracker,
    IWorkspaceGitChangesMonitoringPause monitoringPause,
    WorkerConnectionTracker connectionTracker,
    IOptions<GitChangesOptions> gitChangesOptions,
    ILogger<GitChangesMonitoringBackgroundService> logger) : BackgroundService
{
    private readonly SemaphoreSlim _wake = new(0, 1);
    private long? _lastSweepStartedAt;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Trigger an immediate sweep on Worker (re)connect, per the design's "Worker reconnect" trigger,
        // instead of waiting for the next renewal interval.
        connectionTracker.OnStateChanged(state =>
        {
            logger.LogInformation("[GCDiag] Worker connection state changed: {State}", state);
            if (state == WorkerConnectionState.Online)
            {
                TryWake();
            }
        });

        var options = gitChangesOptions.Value;
        var renewalMinutes = Math.Clamp(options.WatcherRenewalIntervalMinutes, 1, Math.Max(1, options.WatcherIdleGraceMinutes - 1));
        var renewalInterval = TimeSpan.FromMinutes(renewalMinutes);

        try
        {
            // Give the host a moment to finish starting before the first sweep.
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await MonitorActiveWorkspacesAsync(stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Git Changes monitoring sweep failed");
                }

                await Task.WhenAny(Task.Delay(renewalInterval, stoppingToken), _wake.WaitAsync(stoppingToken));
                while (_wake.CurrentCount > 0)
                {
                    _wake.Wait(0);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    private void TryWake()
    {
        try
        {
            if (_wake.CurrentCount == 0)
            {
                _wake.Release();
            }
        }
        catch (SemaphoreFullException)
        {
            // Another wake is already pending; nothing to do.
        }
        catch (ObjectDisposedException)
        {
            // Service is shutting down.
        }
    }

    private async Task EnsureWatchersAsync(
        IServiceProvider services, int workspaceId, WorkspaceFeatureContextId contextId, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            var targets = await GitChangesMonitorTargets.ResolveAsync(
                services.GetRequiredService<AppDbContext>(),
                services.GetRequiredService<IWorkspaceContextPathResolver>(),
                workspaceId, contextId, null, cancellationToken);
            if (targets.Count == 0)
            {
                return;
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, gitChangesOptions.Value.WatcherEnsureTimeoutSeconds)));
            var first = targets[0];
            var result = await services.GetRequiredService<IGitChangesWorkerClient>().EnsureWatchersAsync(
                first.Root, first.WorkspaceName, first.WorkspaceRepositoryName, workspaceId,
                targets.Select(t => new GitChangesEnsureRepository(t.RepositoryId, t.RepositoryName)).ToList(),
                timeout.Token);

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
                workspaceId, contextId.Value, targets.Count, counts, result.WorkerInstanceId, result.EffectiveGraceSeconds,
                (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("[GCDiag] ensure watchers timed out: workspace={WorkspaceId} context={ContextId}", workspaceId, contextId.Value);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "[GCDiag] ensure watchers threw: workspace={WorkspaceId} context={ContextId}", workspaceId, contextId.Value);
        }
    }

    private async Task MonitorActiveWorkspacesAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var sweepStarted = Stopwatch.GetTimestamp();
        var sinceLastSweep = _lastSweepStartedAt is { } last ? Stopwatch.GetElapsedTime(last) : (TimeSpan?)null;
        _lastSweepStartedAt = sweepStarted;

        var workerBridge = scope.ServiceProvider.GetRequiredService<IWorkerBridge>();
        if (!workerBridge.IsWorkerConnected)
        {
            logger.LogInformation(
                "[GCDiag] sweep skipped: Worker is not connected (sinceLastSweepStartSec={SinceLastSweepSec})",
                sinceLastSweep?.TotalSeconds);
            return;
        }

        var contextResolver = scope.ServiceProvider.GetRequiredService<IWorkspaceFeatureContextResolver>();
        var activeWorkspaceIds = activityTracker.GetActiveWorkspaceIds();
        if (activeWorkspaceIds.Count == 0)
        {
            logger.LogDebug("[GCDiag] sweep skipped: no active workspaces");
            return;
        }

        // Planned cadence is the renewal interval; sinceLastSweepStart much larger than that means the
        // previous sweep ran long (watcher idle grace is only 10 minutes by default).
        logger.LogInformation(
            "[GCDiag] sweep start: activeWorkspaces=[{WorkspaceIds}] sinceLastSweepStartSec={SinceLastSweepSec}",
            string.Join(",", activeWorkspaceIds),
            sinceLastSweep?.TotalSeconds);

        // Scanned sequentially: each ScanWorkspaceAsync call is already internally bounded to
        // MaxParallelRepositoryOperations, so looping (rather than fanning all workspaces out at once)
        // keeps the sweep's total concurrent Worker status scans within that same bound.
        foreach (var workspaceId in activeWorkspaceIds)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var contexts = await contextResolver.ListForWorkspaceAsync(workspaceId, cancellationToken);
                foreach (var ctx in contexts)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    // D2: a Feature context being removed is paused here so the sweep never asks the
                    // Worker to scan a worktree that Remove Feature is deleting at the same time. The
                    // special Workspace context is never paused, so its own monitoring is unaffected.
                    if (monitoringPause.IsPaused(ctx.ContextId.Value))
                    {
                        logger.LogDebug("[GCDiag] sweep context skipped (paused for removal): workspace={WorkspaceId} context={ContextId}", workspaceId, ctx.ContextId.Value);
                        continue;
                    }

                    var contextStarted = Stopwatch.GetTimestamp();
                    if (gitChangesOptions.Value.EnsureWatchersInSweep)
                    {
                        await EnsureWatchersAsync(scope.ServiceProvider, workspaceId, ctx.ContextId, cancellationToken);
                    }

                    await scanner.ScanWorkspaceAsync(workspaceId, ctx.ContextId, cancellationToken);
                    logger.LogInformation(
                        "[GCDiag] sweep context done: workspace={WorkspaceId} context={ContextId} elapsedMs={ElapsedMs}",
                        workspaceId, ctx.ContextId.Value, (long)Stopwatch.GetElapsedTime(contextStarted).TotalMilliseconds);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Git Changes monitoring sweep failed for workspace {WorkspaceId}", workspaceId);
            }
        }

        var elapsed = Stopwatch.GetElapsedTime(sweepStarted);
        var renewalInterval = TimeSpan.FromMinutes(Math.Clamp(
            gitChangesOptions.Value.WatcherRenewalIntervalMinutes, 1, Math.Max(1, gitChangesOptions.Value.WatcherIdleGraceMinutes - 1)));
        var idleGrace = TimeSpan.FromMinutes(Math.Max(1, gitChangesOptions.Value.WatcherIdleGraceMinutes));

        // Next sweep starts after this one plus the renewal delay; if that exceeds the Worker's idle grace,
        // watchers for repositories scanned early in this sweep can expire before they are renewed.
        if (elapsed + renewalInterval >= idleGrace)
        {
            logger.LogWarning(
                "[GCDiag] sweep slow: elapsedSec={ElapsedSec} plus renewal {RenewalSec}s reaches watcher idle grace {GraceSec}s; Worker watchers may expire before renewal",
                (long)elapsed.TotalSeconds, (long)renewalInterval.TotalSeconds, (long)idleGrace.TotalSeconds);
        }
        else
        {
            logger.LogInformation("[GCDiag] sweep end: elapsedSec={ElapsedSec}", (long)elapsed.TotalSeconds);
        }
    }
}
