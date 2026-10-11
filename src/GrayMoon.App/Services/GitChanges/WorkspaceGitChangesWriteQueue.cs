using System.Threading.Channels;
using GrayMoon.Common.Git;

namespace GrayMoon.App.Services.GitChanges;

/// <summary>
/// Decouples incoming Worker Git Changes snapshot pushes from the SignalR hub invocation slot and
/// serializes all resulting SQLite writes through one background worker - mirrors
/// <see cref="WorkerSyncNotificationQueue"/>'s existing shape for the same reason: up to 16 repositories
/// may be scanned concurrently on the Worker, but SQLite has limited write concurrency.
/// </summary>
public class WorkspaceGitChangesWriteQueue(
    IServiceScopeFactory scopeFactory,
    ILogger<WorkspaceGitChangesWriteQueue> logger) : BackgroundService
{
    private readonly Channel<QueuedSnapshot> _channel =
        Channel.CreateUnbounded<QueuedSnapshot>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });

    /// <summary>Enqueues a notification for background processing. Always returns before any DB work begins.</summary>
    public virtual bool Enqueue(GitChangesSnapshotNotification notification) =>
        _channel.Writer.TryWrite(new QueuedSnapshot(notification, Completed: null));

    /// <summary>
    /// Enqueues a notification and waits until the single reader has finished persisting it.
    /// Callers that must reload from SQLite afterwards await this instead of calling the handler themselves.
    /// </summary>
    public virtual async Task EnqueueAndWaitAsync(
        GitChangesSnapshotNotification notification,
        CancellationToken cancellationToken = default)
    {
        var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_channel.Writer.TryWrite(new QueuedSnapshot(notification, completed)))
            throw new InvalidOperationException("Git Changes write queue is stopped.");

        await using var registration = cancellationToken.Register(() => completed.TrySetCanceled(cancellationToken));
        await completed.Task;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("WorkspaceGitChangesWriteQueue starting");

        await foreach (var item in _channel.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var handler = scope.ServiceProvider.GetRequiredService<GitChangesSnapshotPushHandler>();
                await handler.HandleAsync(item.Notification, stoppingToken);
                item.Completed?.TrySetResult(true);
            }
            catch (OperationCanceledException)
            {
                item.Completed?.TrySetCanceled(stoppingToken);
                throw;
            }
            catch (Exception ex)
            {
                item.Completed?.TrySetException(ex);
                logger.LogError(
                    ex,
                    "Git Changes snapshot processing failed for workspace {WorkspaceId} repository {RepositoryId}",
                    item.Notification.WorkspaceId,
                    item.Notification.RepositoryId);
            }
        }

        logger.LogInformation("WorkspaceGitChangesWriteQueue stopped");
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        _channel.Writer.TryComplete();
        return base.StopAsync(cancellationToken);
    }

    private sealed record QueuedSnapshot(GitChangesSnapshotNotification Notification, TaskCompletionSource<bool>? Completed);
}
