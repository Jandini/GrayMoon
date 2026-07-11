using GrayMoon.App.Data;
using GrayMoon.Common.Git;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GrayMoon.App.Services.GitChanges;

/// <summary>
/// Owns the Git Changes background monitoring policy. Per the feature's design, a repository's
/// Agent-side <c>FileSystemWatcher</c> lease belongs to the workspace background service, not the
/// browser page - opening or closing the Git Changes page must never be what starts or stops
/// monitoring. This sweep periodically calls <c>GetGitChangeStatus</c> for every repository across
/// every workspace with a resolvable root path, which both seeds/renews the Agent's
/// <c>GitRepositoryWatcherManager</c> lease (idle grace period is
/// <see cref="GitChangesOptions.WatcherIdleGraceMinutes"/>) and keeps the persisted SQLite projection
/// fresh even when nobody has the page open. Results flow through the same
/// <see cref="WorkspaceGitChangesWriteQueue"/> used for watcher-driven pushes, so there is only one
/// write path into the projection.
/// </summary>
public sealed class GitChangesMonitoringBackgroundService(
    IServiceScopeFactory scopeFactory,
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
                    await MonitorAllWorkspacesAsync(stoppingToken);
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

    private async Task MonitorAllWorkspacesAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var agentBridge = scope.ServiceProvider.GetRequiredService<IAgentBridge>();
        if (!agentBridge.IsAgentConnected)
        {
            return;
        }

        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var workspaceService = scope.ServiceProvider.GetRequiredService<WorkspaceService>();
        var agentClient = scope.ServiceProvider.GetRequiredService<IGitChangesAgentClient>();
        var writeQueue = scope.ServiceProvider.GetRequiredService<WorkspaceGitChangesWriteQueue>();

        var links = await dbContext.WorkspaceRepositories
            .Include(l => l.Workspace)
            .Include(l => l.Repository)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        var targets = new List<MonitorTarget>();
        foreach (var link in links)
        {
            if (link.Workspace == null || link.Repository == null)
            {
                continue;
            }

            var root = await workspaceService.GetRootPathForWorkspaceAsync(link.Workspace, cancellationToken);
            if (string.IsNullOrWhiteSpace(root))
            {
                continue;
            }

            targets.Add(new MonitorTarget(root, link.Workspace.Name, link.Repository.RepositoryName, link.WorkspaceId, link.RepositoryId));
        }

        if (targets.Count == 0)
        {
            return;
        }

        using var semaphore = new SemaphoreSlim(Math.Max(1, gitChangesOptions.Value.MaxParallelRepositoryOperations));

        var tasks = targets.Select(async target =>
        {
            await semaphore.WaitAsync(cancellationToken);
            try
            {
                var result = await agentClient.GetStatusAsync(
                    target.Root, target.WorkspaceName, target.RepositoryName,
                    target.WorkspaceId, target.RepositoryId, forceRefresh: false, cancellationToken);

                if (result.Success && result.Snapshot != null)
                {
                    writeQueue.Enqueue(new GitChangesSnapshotNotification
                    {
                        WorkspaceId = target.WorkspaceId,
                        RepositoryId = target.RepositoryId,
                        Snapshot = result.Snapshot,
                    });
                }
                else if (!result.Success)
                {
                    logger.LogDebug(
                        "Git Changes monitoring status check failed for {WorkspaceName}/{RepositoryName}: {ErrorCode} {ErrorMessage}",
                        target.WorkspaceName, target.RepositoryName, result.ErrorCode, result.ErrorMessage);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex, "Git Changes monitoring status check threw for {WorkspaceName}/{RepositoryName}",
                    target.WorkspaceName, target.RepositoryName);
            }
            finally
            {
                semaphore.Release();
            }
        });

        await Task.WhenAll(tasks);
    }

    private sealed record MonitorTarget(string Root, string WorkspaceName, string RepositoryName, int WorkspaceId, int RepositoryId);
}
