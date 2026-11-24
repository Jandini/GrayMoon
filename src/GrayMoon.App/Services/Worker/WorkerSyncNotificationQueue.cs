using System.Diagnostics;
using System.Threading.Channels;
using GrayMoon.Abstractions.Notifications;
using GrayMoon.Application.Features;

namespace GrayMoon.App.Services.Worker;

/// <summary>
/// Singleton BackgroundService that decouples incoming <see cref="RepositorySyncNotification"/> messages
/// from the SignalR hub invocation slot. The hub's SyncCommand method enqueues immediately and returns,
/// keeping hub slots free so ResponseCommand messages from the worker are never blocked.
/// </summary>
/// <remarks>
/// One reader applies the notifications in arrival order. The state of each repository is stored as its notification
/// is read (<see cref="ISyncNotificationHandler.ApplyAsync"/>); the recompute of the context and the broadcasts run
/// once for every run of consecutive notifications of one context (<see cref="ISyncNotificationHandler.CompleteAsync"/>).
/// A run ends when the channel is empty, when a notification of another context arrives, at
/// <see cref="MaxBatchSize"/> notifications, or on stop. A notification stays pending for
/// <see cref="IWorkerSyncNotificationBarrier"/> and the UI count until the completion that covers it has returned.
/// </remarks>
public sealed class WorkerSyncNotificationQueue(
    IServiceScopeFactory scopeFactory,
    AppBackgroundWorkStateService appBackgroundWork,
    ILogger<WorkerSyncNotificationQueue> logger) : BackgroundService, IWorkerSyncNotificationBarrier
{
    /// <summary>An item whose queue wait plus apply time is above this is logged as a warning.</summary>
    internal static readonly TimeSpan SlowItemThreshold = TimeSpan.FromSeconds(2);

    /// <summary>
    /// The most notifications one completion covers. It bounds how long the first notification of an endless stream
    /// waits for its recompute.
    /// </summary>
    internal const int MaxBatchSize = 64;

    private readonly object _pendingLock = new();
    private readonly Dictionary<int, int> _pendingByWorkspace = new();
    private readonly Dictionary<int, List<TaskCompletionSource>> _waitersByWorkspace = new();

    private readonly Channel<QueuedSync> _channel =
        Channel.CreateUnbounded<QueuedSync>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

    /// <param name="ReceivedAt">A Stopwatch timestamp (<see cref="RepositoryStateFreshness.Now"/>) read on arrival.</param>
    private sealed record QueuedSync(RepositorySyncNotification Notification, long ReceivedAt)
    {
        public long StartedAt { get; set; }
    }

    /// <summary>Notifications of one context whose state is stored and whose completion has not run yet.</summary>
    private sealed class OpenBatch
    {
        public (int WorkspaceId, WorkspaceFeatureContextId ContextId)? Key { get; private set; }
        public List<(QueuedSync Item, AppliedSync Applied)> Items { get; } = [];

        public void Add(QueuedSync item, AppliedSync applied)
        {
            Key = (applied.WorkspaceId, applied.ContextId);
            Items.Add((item, applied));
        }

        public List<(QueuedSync Item, AppliedSync Applied)> Take()
        {
            var taken = Items.ToList();
            Items.Clear();
            Key = null;
            return taken;
        }
    }

    /// <summary>Enqueues a notification for background processing. Always returns before any DB or worker work begins.</summary>
    public bool Enqueue(RepositorySyncNotification notification)
    {
        // Counted before the write so a barrier can never observe the notification as neither pending nor applied.
        MarkPending(notification.WorkspaceId);
        // Stamped on arrival: the handler compares it with operations that began while the notification waited here.
        if (_channel.Writer.TryWrite(new QueuedSync(notification, RepositoryStateFreshness.Now())))
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
        appBackgroundWork.Increment(workspaceId);
    }

    private void MarkApplied(int workspaceId)
    {
        // Visibility only: the UI count. The barrier below keeps its own count.
        appBackgroundWork.Decrement(workspaceId);

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

        // Single reader: notifications of one repository are applied in arrival order (snapshots are partial, so
        // none is dropped in favour of a later one) and state writes never race each other.
        var batch = new OpenBatch();
        try
        {
            await foreach (var item in _channel.Reader.ReadAllAsync(stoppingToken))
            {
                await ApplyAsync(item, batch, stoppingToken);

                // Nothing else is waiting, or the batch is full: recompute now. A notification that arrives during
                // the completion starts the next batch.
                if (batch.Items.Count >= MaxBatchSize || !_channel.Reader.TryPeek(out _))
                    await CompleteAsync(batch, stoppingToken);
            }
        }
        finally
        {
            // Stopping with stored but not completed notifications: nothing waits for them any more.
            foreach (var (item, _) in batch.Take())
                Release(item);
            ReleaseAllWaiters();
            logger.LogInformation("WorkerSyncNotificationQueue stopped");
        }
    }

    /// <summary>
    /// Stores one notification's state. It joins <paramref name="batch"/> when its state was stored; a notification
    /// that was skipped, superseded or failed before its state write has nothing to recompute and is released here.
    /// </summary>
    private async Task ApplyAsync(QueuedSync item, OpenBatch batch, CancellationToken stoppingToken)
    {
        item.StartedAt = Stopwatch.GetTimestamp();
        var observer = new BatchObserver(this, batch, stoppingToken);
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var handler = scope.ServiceProvider.GetRequiredService<ISyncNotificationHandler>();
            await handler.ApplyAsync(item.Notification, item.ReceivedAt, observer);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutdown. Any other cancellation (a check this item joined was aborted by its user) is a failed item.
            Join(item, batch, observer);
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "SyncNotification processing failed for workspace {WorkspaceId} repo {RepositoryId}",
                item.Notification.WorkspaceId, item.Notification.RepositoryId);
        }

        Join(item, batch, observer);
    }

    private void Join(QueuedSync item, OpenBatch batch, BatchObserver observer)
    {
        if (observer.Applied is { } applied)
            batch.Add(item, applied);
        else
            Release(item);
    }

    /// <summary>
    /// Runs the completion for the open batch and releases its notifications, whether the completion returned or
    /// threw: the barrier means "state stored and the recompute that covers it has run".
    /// </summary>
    private async Task CompleteAsync(OpenBatch batch, CancellationToken stoppingToken)
    {
        if (batch.Items.Count == 0)
            return;

        var items = batch.Take();
        var first = items[0].Applied;
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var handler = scope.ServiceProvider.GetRequiredService<ISyncNotificationHandler>();
            await handler.CompleteAsync(items.Select(i => i.Applied).ToList());
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Recompute after {Count} sync notification(s) failed for workspace {WorkspaceId} context {ContextId}",
                items.Count, first.WorkspaceId, first.ContextId.Value);
        }
        finally
        {
            foreach (var (item, _) in items)
                Release(item);
        }
    }

    /// <summary>Ends one notification: the barrier count and the UI count together, exactly once per notification.</summary>
    private void Release(QueuedSync item)
    {
        MarkApplied(item.Notification.WorkspaceId);

        var waited = Stopwatch.GetElapsedTime(item.ReceivedAt, item.StartedAt);
        var applied = Stopwatch.GetElapsedTime(item.StartedAt);
        if (waited + applied > SlowItemThreshold)
        {
            logger.LogWarning(
                "Slow sync notification: workspace {WorkspaceId} repo {RepositoryId} waited {WaitMs} ms in the queue and took {ApplyMs} ms to apply",
                item.Notification.WorkspaceId, item.Notification.RepositoryId, (long)waited.TotalMilliseconds, (long)applied.TotalMilliseconds);
        }
    }

    /// <summary>Connects one apply to the open batch.</summary>
    private sealed class BatchObserver(WorkerSyncNotificationQueue queue, OpenBatch batch, CancellationToken stoppingToken)
        : ISyncApplyObserver
    {
        public AppliedSync? Applied { get; private set; }

        public async Task ContextResolvedAsync(int workspaceId, WorkspaceFeatureContextId contextId)
        {
            // A batch covers one context. Completing the other context's batch before this notification can wait
            // (for a Feature that is still being created) keeps that context from waiting with it.
            if (batch.Key is { } key && key != (workspaceId, contextId))
                await queue.CompleteAsync(batch, stoppingToken);
        }

        public void StateWritten(AppliedSync applied) => Applied = applied;
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        _channel.Writer.TryComplete();
        ReleaseAllWaiters();
        return base.StopAsync(cancellationToken);
    }
}
