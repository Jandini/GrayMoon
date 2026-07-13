using System.Threading.Channels;
using GrayMoon.Common.Git;

namespace GrayMoon.App.Services.GitChanges;

/// <summary>
/// Decouples incoming Agent Git Changes snapshot pushes from the SignalR hub invocation slot and
/// serializes all resulting SQLite writes through one background worker - mirrors
/// <see cref="AgentSyncNotificationQueue"/>'s existing shape for the same reason: up to 16 repositories
/// may be scanned concurrently on the Agent, but SQLite has limited write concurrency.
/// </summary>
public sealed class WorkspaceGitChangesWriteQueue(
    IServiceScopeFactory scopeFactory,
    ILogger<WorkspaceGitChangesWriteQueue> logger) : BackgroundService
{
    private readonly Channel<GitChangesSnapshotNotification> _channel =
        Channel.CreateUnbounded<GitChangesSnapshotNotification>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });

    /// <summary>Enqueues a notification for background processing. Always returns before any DB work begins.</summary>
    public bool Enqueue(GitChangesSnapshotNotification notification) =>
        _channel.Writer.TryWrite(notification);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("WorkspaceGitChangesWriteQueue starting");

        await foreach (var notification in _channel.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var handler = scope.ServiceProvider.GetRequiredService<GitChangesSnapshotPushHandler>();
                await handler.HandleAsync(notification, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(
                    ex,
                    "Git Changes snapshot processing failed for workspace {WorkspaceId} repository {RepositoryId}",
                    notification.WorkspaceId,
                    notification.RepositoryId);
            }
        }

        logger.LogInformation("WorkspaceGitChangesWriteQueue stopped");
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        _channel.Writer.TryComplete();
        return base.StopAsync(cancellationToken);
    }
}
