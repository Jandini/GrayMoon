using GrayMoon.Common.Git;
using Microsoft.Extensions.Options;

namespace GrayMoon.App.Services.GitChanges;

/// <summary>
/// Owns the Git Changes background monitoring policy. Per the feature's design, a repository's
/// Agent-side <c>FileSystemWatcher</c> lease belongs to the workspace background service, not the
/// browser page - opening or closing the Git Changes page must never directly start or stop
/// monitoring. This sweep periodically calls <c>GetGitChangeStatus</c> for every repository in every
/// <i>actively viewed</i> workspace (per <see cref="IWorkspaceGitChangesActivityTracker"/>) - not every
/// workspace in the database - which both seeds/renews the Agent's <c>GitRepositoryWatcherManager</c>
/// lease (idle grace period is <see cref="GitChangesOptions.WatcherIdleGraceMinutes"/>) and keeps the
/// persisted SQLite projection fresh while a workspace is in view. Workspaces with no recent viewer fall
/// out of scope on their own once <see cref="GitChangesOptions.WorkspaceActivityGraceMinutes"/> elapses,
/// so this never blasts every repository across every workspace regardless of whether anyone is looking.
/// The actual per-workspace scan (also used for on-open warm-up and manual Refresh) lives in
/// <see cref="IGitChangesWorkspaceScanner"/>.
/// </summary>
public sealed class GitChangesMonitoringBackgroundService(
    IServiceScopeFactory scopeFactory,
    IGitChangesWorkspaceScanner scanner,
    IWorkspaceGitChangesActivityTracker activityTracker,
    AgentConnectionTracker connectionTracker,
    IOptions<GitChangesOptions> gitChangesOptions,
    ILogger<GitChangesMonitoringBackgroundService> logger) : BackgroundService
{
    private readonly SemaphoreSlim _wake = new(0, 1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Trigger an immediate sweep on Agent (re)connect, per the design's "Agent reconnect" trigger,
        // instead of waiting for the next renewal interval.
        connectionTracker.OnStateChanged(state =>
        {
            if (state == AgentConnectionState.Online)
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

    private async Task MonitorActiveWorkspacesAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var agentBridge = scope.ServiceProvider.GetRequiredService<IAgentBridge>();
        if (!agentBridge.IsAgentConnected)
        {
            return;
        }

        var activeWorkspaceIds = activityTracker.GetActiveWorkspaceIds();
        if (activeWorkspaceIds.Count == 0)
        {
            return;
        }

        // Scanned sequentially: each ScanWorkspaceAsync call is already internally bounded to
        // MaxParallelRepositoryOperations, so looping (rather than fanning all workspaces out at once)
        // keeps the sweep's total concurrent Agent status scans within that same bound.
        foreach (var workspaceId in activeWorkspaceIds)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await scanner.ScanWorkspaceAsync(workspaceId, cancellationToken);
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
    }
}
