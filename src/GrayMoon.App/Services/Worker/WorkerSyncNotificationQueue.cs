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
/// The reader never waits for a Feature that is still being created or repaired: the notifications of that context
/// are set aside, stay pending, and are applied in arrival order when the Feature has finished, while the
/// notifications of every other context go on.
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

    private readonly Channel<QueueEntry> _channel =
        Channel.CreateUnbounded<QueueEntry>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

    /// <summary>
    /// Notifications set aside because the Feature of their context is still being created or repaired, in arrival
    /// order per context. Read and written by the reader only.
    /// </summary>
    private readonly Dictionary<(int WorkspaceId, WorkspaceFeatureContextId ContextId), List<QueuedSync>> _parked = new();

    /// <summary>What the reader takes from the channel: a notification, or the end of a Feature's finalization.</summary>
    private abstract record QueueEntry;

    /// <param name="ReceivedAt">A Stopwatch timestamp (<see cref="RepositoryStateFreshness.Now"/>) read on arrival.</param>
    private sealed record QueuedSync(RepositorySyncNotification Notification, long ReceivedAt) : QueueEntry
    {
        public long StartedAt { get; set; }

        /// <summary>When it was last set aside for its Feature (a Stopwatch timestamp).</summary>
        public long ParkedAt { get; set; }

        /// <summary>Stopwatch ticks spent set aside for its Feature. That is not time the queue kept it waiting.</summary>
        public long ParkedTicks { get; set; }
    }

    /// <summary>
    /// The Feature of this context is no longer being created or repaired, so its parked notifications are applied
    /// now. Written to the channel, so they keep their place before every notification that arrives later.
    /// </summary>
    /// <param name="Finalized">False when the wait for the Feature ran out: the notifications are applied anyway.</param>
    private sealed record ContextReleased(int WorkspaceId, WorkspaceFeatureContextId ContextId, bool Finalized) : QueueEntry;

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
            await foreach (var entry in _channel.Reader.ReadAllAsync(stoppingToken))
            {
                if (entry is ContextReleased released)
                    await ApplyParkedAsync(released, batch, stoppingToken);
                else
                    await ApplyAsync((QueuedSync)entry, batch, applyWhileFinalizing: false, stoppingToken);

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
            // The same for the ones still set aside for a Feature that never finished.
            foreach (var item in _parked.Values.SelectMany(items => items).ToList())
                Release(item);
            _parked.Clear();
            ReleaseAllWaiters();
            logger.LogInformation("WorkerSyncNotificationQueue stopped");
        }
    }

    /// <summary>
    /// Sets <paramref name="item"/> aside until the Feature of its context is ready for it. The first one of a context
    /// brings the wait for that Feature; when it ends, a <see cref="ContextReleased"/> entry is written to the channel.
    /// The reader never waits for a Feature itself: Create Feature held one notification for more than two minutes,
    /// and every notification of every other context waited behind it (logs of 2026-11-24, 61 slow notifications).
    /// </summary>
    private void Park(
        (int WorkspaceId, WorkspaceFeatureContextId ContextId) key, QueuedSync item, Task<bool>? finalization)
    {
        item.ParkedAt = Stopwatch.GetTimestamp();
        if (_parked.TryGetValue(key, out var parked))
        {
            parked.Add(item);
            return;
        }

        _parked[key] = [item];
        _ = PostReleaseAsync(key, finalization!);
    }

    private async Task PostReleaseAsync((int WorkspaceId, WorkspaceFeatureContextId ContextId) key, Task<bool> finalization)
    {
        var finalized = false;
        try
        {
            finalized = await finalization;
        }
        catch (Exception ex)
        {
            // The wait itself failed. The notifications are applied anyway, as after a wait that ran out.
            logger.LogError(ex,
                "Waiting for the Feature of workspace {WorkspaceId} context {ContextId} failed; applying its sync notifications anyway",
                key.WorkspaceId, key.ContextId.Value);
        }

        // False only after stop, when the parked notifications were released with the reader.
        _channel.Writer.TryWrite(new ContextReleased(key.WorkspaceId, key.ContextId, finalized));
    }

    /// <summary>
    /// Applies the notifications that were set aside for a Feature, in arrival order. A Feature that is being repaired
    /// again by now takes them back, unless the wait ran out (<see cref="ContextReleased.Finalized"/> false).
    /// </summary>
    private async Task ApplyParkedAsync(ContextReleased released, OpenBatch batch, CancellationToken stoppingToken)
    {
        if (!_parked.Remove((released.WorkspaceId, released.ContextId), out var items))
            return;

        logger.LogDebug(
            "Applying {Count} sync notification(s) held for the Feature of workspace {WorkspaceId} context {ContextId}",
            items.Count, released.WorkspaceId, released.ContextId.Value);
        var now = Stopwatch.GetTimestamp();
        foreach (var item in items)
        {
            item.ParkedTicks += now - item.ParkedAt;
            await ApplyAsync(item, batch, applyWhileFinalizing: !released.Finalized, stoppingToken);
            if (batch.Items.Count >= MaxBatchSize)
                await CompleteAsync(batch, stoppingToken);
        }
    }

    /// <summary>
    /// Stores one notification's state. It joins <paramref name="batch"/> when its state was stored; a notification
    /// that was skipped, superseded or failed before its state write has nothing to recompute and is released here.
    /// One whose Feature is still being created or repaired is set aside (<see cref="Park"/>) and stays pending.
    /// </summary>
    private async Task ApplyAsync(QueuedSync item, OpenBatch batch, bool applyWhileFinalizing, CancellationToken stoppingToken)
    {
        item.StartedAt = Stopwatch.GetTimestamp();
        var observer = new BatchObserver(this, batch, item, applyWhileFinalizing, stoppingToken);
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
        // Set aside for its Feature: it is applied again later and stays pending until then.
        if (observer.Parked)
            return;

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

        var waited = QueueWait(item.ReceivedAt, item.StartedAt, item.ParkedTicks);
        var applied = Stopwatch.GetElapsedTime(item.StartedAt);
        if (waited + applied > SlowItemThreshold)
        {
            logger.LogWarning(
                "Slow sync notification: workspace {WorkspaceId} repo {RepositoryId} waited {WaitMs} ms in the queue and took {ApplyMs} ms to apply",
                item.Notification.WorkspaceId, item.Notification.RepositoryId, (long)waited.TotalMilliseconds, (long)applied.TotalMilliseconds);
        }
    }

    /// <summary>
    /// How long the queue itself kept a notification before its last apply began: the time since it arrived, less
    /// the time it was set aside for a Feature that was still being created or repaired.
    /// </summary>
    internal static TimeSpan QueueWait(long receivedAt, long startedAt, long parkedTicks)
    {
        var waited = Stopwatch.GetElapsedTime(receivedAt, startedAt) - Stopwatch.GetElapsedTime(0, parkedTicks);
        return waited < TimeSpan.Zero ? TimeSpan.Zero : waited;
    }

    /// <summary>Connects one apply to the open batch.</summary>
    private sealed class BatchObserver(
        WorkerSyncNotificationQueue queue,
        OpenBatch batch,
        QueuedSync item,
        bool applyWhileFinalizing,
        CancellationToken stoppingToken)
        : ISyncApplyObserver
    {
        public AppliedSync? Applied { get; private set; }

        /// <summary>The notification was set aside for its Feature instead of being applied.</summary>
        public bool Parked { get; private set; }

        public async Task<bool> ContextResolvedAsync(int workspaceId, WorkspaceFeatureContextId contextId, Task<bool>? finalization)
        {
            var key = (workspaceId, contextId);

            // Earlier notifications of this context are set aside: this one goes behind them, also when the Feature
            // has finished meanwhile (their release is then still in the channel), so arrival order is kept.
            // Otherwise it is set aside when its Feature is not ready, unless the wait for that Feature ran out.
            if (queue._parked.ContainsKey(key) || (finalization is not null && !applyWhileFinalizing))
            {
                queue.Park(key, item, finalization);
                Parked = true;
                return false;
            }

            // A batch covers one context: the other context's batch is completed before this state write.
            if (batch.Key is { } open && open != key)
                await queue.CompleteAsync(batch, stoppingToken);
            return true;
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
