using System.Threading.Channels;
using GrayMoon.Abstractions.Notifications;

namespace GrayMoon.App.Services;

/// <summary>
/// Singleton BackgroundService that decouples incoming <see cref="RepositorySyncNotification"/> messages
/// from the SignalR hub invocation slot. The hub's SyncCommand method enqueues immediately and returns,
/// keeping hub slots free so ResponseCommand messages from the agent are never blocked.
/// </summary>
public sealed class AgentSyncNotificationQueue(
    IServiceScopeFactory scopeFactory,
    ILogger<AgentSyncNotificationQueue> logger) : BackgroundService
{
    private readonly Channel<RepositorySyncNotification> _channel =
        Channel.CreateUnbounded<RepositorySyncNotification>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

    /// <summary>Enqueues a notification for background processing. Always returns before any DB or agent work begins.</summary>
    public bool Enqueue(RepositorySyncNotification notification) =>
        _channel.Writer.TryWrite(notification);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("AgentSyncNotificationQueue starting");

        // Single worker: serializes processing so concurrent SyncCommands for the same workspace
        // don't race on DB writes. CheckAndPersistFileVersionStatusAsync already coalesces
        // concurrent callers per workspace, so burst hooks still result in only one agent round-trip.
        await foreach (var notification in _channel.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var handler = scope.ServiceProvider.GetRequiredService<SyncCommandHandler>();
                await handler.HandleAsync(notification);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "SyncNotification processing failed for workspace {WorkspaceId} repo {RepositoryId}",
                    notification.WorkspaceId, notification.RepositoryId);
            }
        }

        logger.LogInformation("AgentSyncNotificationQueue stopped");
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        _channel.Writer.TryComplete();
        return base.StopAsync(cancellationToken);
    }
}
