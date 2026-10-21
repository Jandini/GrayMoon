using System.Threading.Channels;
using GrayMoon.Abstractions.Notifications;

namespace GrayMoon.App.Services.Worker;

/// <summary>
/// Singleton BackgroundService that decouples incoming <see cref="RepositorySyncNotification"/> messages
/// from the SignalR hub invocation slot. The hub's SyncCommand method enqueues immediately and returns,
/// keeping hub slots free so ResponseCommand messages from the worker are never blocked.
/// </summary>
public sealed class WorkerSyncNotificationQueue(
    IServiceScopeFactory scopeFactory,
    ILogger<WorkerSyncNotificationQueue> logger) : BackgroundService, IWorkerSyncNotificationBarrier
{
    private readonly object _pendingLock = new();
    private readonly Dictionary<int, int> _pendingByWorkspace = new();
    private readonly Dictionary<int, List<TaskCompletionSource>> _waitersByWorkspace = new();

    private readonly Channel<RepositorySyncNotification> _channel =
        Channel.CreateUnbounded<RepositorySyncNotification>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

    /// <summary>Enqueues a notification for background processing. Always returns before any DB or worker work begins.</summary>
    public bool Enqueue(RepositorySyncNotification notification)
    {
        // Counted before the write so a barrier can never observe the notification as neither pending nor applied.
        MarkPending(notification.WorkspaceId);
        if (_channel.Writer.TryWrite(notification))
            return true;

        MarkApplied(notification.WorkspaceId);
        return false;
    }

    public Task WaitForPendingAsync(int workspaceId, CancellationToken cancellationToken)
    {
        TaskCompletionSource waiter;
        lock (_pendingLock)
        {
            if (!_pendingByWorkspace.TryGetValue(workspaceId, out var pending) || pending == 0)
                return Task.CompletedTask;

            waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_waitersByWorkspace.TryGetValue(workspaceId, out var waiters))
                _waitersByWorkspace[workspaceId] = waiters = [];
            waiters.Add(waiter);
        }

        return waiter.Task.WaitAsync(cancellationToken);
    }

    private void MarkPending(int workspaceId)
    {
        lock (_pendingLock)
            _pendingByWorkspace[workspaceId] = _pendingByWorkspace.GetValueOrDefault(workspaceId) + 1;
    }

    private void MarkApplied(int workspaceId)
    {
        List<TaskCompletionSource>? release = null;
        lock (_pendingLock)
        {
            var remaining = _pendingByWorkspace.GetValueOrDefault(workspaceId) - 1;
            if (remaining > 0)
            {
                _pendingByWorkspace[workspaceId] = remaining;
                return;
            }

            _pendingByWorkspace.Remove(workspaceId);
            if (_waitersByWorkspace.Remove(workspaceId, out var waiters))
                release = waiters;
        }

        if (release is null)
            return;
        foreach (var waiter in release)
            waiter.TrySetResult();
    }

    private void ReleaseAllWaiters()
    {
        List<TaskCompletionSource> release;
        lock (_pendingLock)
        {
            release = _waitersByWorkspace.Values.SelectMany(w => w).ToList();
            _waitersByWorkspace.Clear();
            _pendingByWorkspace.Clear();
        }

        foreach (var waiter in release)
            waiter.TrySetResult();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("WorkerSyncNotificationQueue starting");

        // Single worker: serializes processing so concurrent SyncCommands for the same workspace
        // don't race on DB writes. CheckAndPersistFileVersionStatusAsync already coalesces
        // concurrent callers per workspace, so burst hooks still result in only one worker round-trip.
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
            finally
            {
                MarkApplied(notification.WorkspaceId);
            }
        }

        ReleaseAllWaiters();
        logger.LogInformation("WorkerSyncNotificationQueue stopped");
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        _channel.Writer.TryComplete();
        ReleaseAllWaiters();
        return base.StopAsync(cancellationToken);
    }
}
