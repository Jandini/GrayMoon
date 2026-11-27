using GrayMoon.Abstractions.Notifications;
using GrayMoon.App.Services.Worker;
using GrayMoon.Application.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace GrayMoon.App.Tests;

/// <summary>
/// The sync queue stores each notification's state as it is read and runs the recompute of a context once per run
/// of consecutive notifications. Create Feature for 27 repositories ran the whole-context recompute 27 times, one
/// after another, and the grid kept changing for 52 seconds after the operation had finished.
/// </summary>
public sealed class WorkerSyncNotificationQueueBatchTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private const string Skipped = "skipped";
    private const string Superseded = "superseded";
    private const string RefsOnly = "refs-only";
    private const string ThrowsBeforeWrite = "throws-before-write";
    private const string ThrowsAfterWrite = "throws-after-write";

    [Fact]
    public async Task A_burst_for_one_context_is_stored_item_by_item_and_completed_once()
    {
        await using var host = new QueueHost();
        for (var repositoryId = 1; repositoryId <= 5; repositoryId++)
            host.Enqueue(context: 10, repositoryId);

        await host.RunUntilIdleAsync();

        Assert.Equal(
            ["apply 10/1", "apply 10/2", "apply 10/3", "apply 10/4", "apply 10/5", "complete 10: 1,2,3,4,5"],
            host.Handler.Log);
    }

    [Fact]
    public async Task A_notification_stays_pending_until_the_completion_that_covers_it_has_returned()
    {
        await using var host = new QueueHost();
        var completion = host.Handler.GateNextCompletion();
        host.Enqueue(context: 10, repositoryId: 1);
        host.Enqueue(context: 10, repositoryId: 2);
        var barrier = host.Queue.WaitForPendingAsync(QueueHost.WorkspaceId, CancellationToken.None);

        await host.Queue.StartAsync(CancellationToken.None);
        await completion.Entered.WaitAsync(Timeout);

        // Both states are stored, the recompute is still running: nothing may be reported as applied yet.
        Assert.False(barrier.IsCompleted);
        Assert.Equal(2, host.State.GetPendingCountForWorkspace(QueueHost.WorkspaceId));

        completion.Release();
        await barrier.WaitAsync(Timeout);
        Assert.Equal(0, host.State.GetTotalPendingCount());
    }

    [Fact]
    public async Task One_failing_notification_does_not_cancel_the_batch()
    {
        await using var host = new QueueHost();
        host.Enqueue(context: 10, repositoryId: 1);
        host.Enqueue(context: 10, repositoryId: 2, behaviour: ThrowsBeforeWrite);
        host.Enqueue(context: 10, repositoryId: 3);

        await host.RunUntilIdleAsync();

        Assert.Equal(
            ["apply 10/1", "apply 10/2", "apply 10/3", "complete 10: 1,3"],
            host.Handler.Log);
        Assert.Equal(0, host.State.GetTotalPendingCount());
    }

    [Fact]
    public async Task A_notification_that_failed_after_its_state_write_is_still_recomputed()
    {
        await using var host = new QueueHost();
        host.Enqueue(context: 10, repositoryId: 1, behaviour: ThrowsAfterWrite);

        await host.RunUntilIdleAsync();

        Assert.Equal(["apply 10/1", "complete 10: 1"], host.Handler.Log);
        Assert.Equal(0, host.State.GetTotalPendingCount());
    }

    [Fact]
    public async Task A_failing_completion_releases_its_notifications_and_the_next_batch_still_runs()
    {
        await using var host = new QueueHost();
        host.Handler.FailNextCompletion(new InvalidOperationException("recompute failed"));
        host.Enqueue(context: 10, repositoryId: 1);
        host.Enqueue(context: 10, repositoryId: 2);

        await host.RunUntilIdleAsync();
        Assert.Equal(0, host.State.GetTotalPendingCount());

        host.Enqueue(context: 10, repositoryId: 3);
        await host.Queue.WaitForPendingAsync(QueueHost.WorkspaceId, CancellationToken.None).WaitAsync(Timeout);

        Assert.Equal(
            ["apply 10/1", "apply 10/2", "complete 10: 1,2", "apply 10/3", "complete 10: 3"],
            host.Handler.Log);
    }

    [Fact]
    public async Task A_cancellation_that_is_not_shutdown_does_not_end_the_reader()
    {
        // The hook's file check can join a check a user job started; aborting that job cancels the shared check.
        await using var host = new QueueHost();
        host.Handler.FailNextCompletion(new OperationCanceledException("a joined check was aborted"));
        host.Enqueue(context: 10, repositoryId: 1);

        await host.RunUntilIdleAsync();

        host.Enqueue(context: 10, repositoryId: 2);
        await host.Queue.WaitForPendingAsync(QueueHost.WorkspaceId, CancellationToken.None).WaitAsync(Timeout);
        Assert.Contains("complete 10: 2", host.Handler.Log);
    }

    [Fact]
    public async Task Two_notifications_for_one_repository_are_both_applied_in_arrival_order_with_their_own_arrival_stamp()
    {
        await using var host = new QueueHost();
        host.Enqueue(context: 10, repositoryId: 1);
        host.Enqueue(context: 10, repositoryId: 1);

        await host.RunUntilIdleAsync();

        Assert.Equal(["apply 10/1", "apply 10/1", "complete 10: 1,1"], host.Handler.Log);
        Assert.Equal(2, host.Handler.ReceivedAt.Count);
        Assert.True(host.Handler.ReceivedAt[0] <= host.Handler.ReceivedAt[1]);
        Assert.All(host.Handler.ReceivedAt, stamp => Assert.NotNull(stamp));
    }

    [Fact]
    public async Task A_batch_is_completed_before_a_notification_of_another_context_can_wait()
    {
        await using var host = new QueueHost();
        // Context 20 is a Feature that is still being created: its notification waits after it was attributed.
        var finalization = host.Handler.GateStateWriteOf(context: 20);
        host.Enqueue(context: 10, repositoryId: 1);
        host.Enqueue(context: 10, repositoryId: 2);
        host.Enqueue(context: 20, repositoryId: 3);
        host.Enqueue(context: 10, repositoryId: 4);

        await host.Queue.StartAsync(CancellationToken.None);
        await finalization.Entered.WaitAsync(Timeout);

        // Context 10 is complete while context 20 still waits.
        Assert.Equal(["apply 10/1", "apply 10/2", "apply 20/3", "complete 10: 1,2"], host.Handler.Log);

        finalization.Release();
        await host.Queue.WaitForPendingAsync(QueueHost.WorkspaceId, CancellationToken.None).WaitAsync(Timeout);

        Assert.Equal(
            [
                "apply 10/1", "apply 10/2", "apply 20/3", "complete 10: 1,2",
                "apply 10/4", "complete 20: 3", "complete 10: 4",
            ],
            host.Handler.Log);
    }

    [Fact]
    public async Task Refs_only_notifications_are_completed_without_asking_for_a_recompute()
    {
        await using var host = new QueueHost();
        host.Enqueue(context: 10, repositoryId: 1, behaviour: RefsOnly);
        host.Enqueue(context: 10, repositoryId: 2, behaviour: RefsOnly);

        await host.RunUntilIdleAsync();

        Assert.Equal(["apply 10/1", "apply 10/2", "complete 10: 1,2"], host.Handler.Log);
        Assert.All(host.Handler.Completed.Single(), applied => Assert.False(applied.NeedsRecompute));
    }

    [Fact]
    public async Task Skipped_and_superseded_notifications_are_released_without_a_completion()
    {
        await using var host = new QueueHost();
        host.Enqueue(context: 10, repositoryId: 1, behaviour: Skipped);
        host.Enqueue(context: 10, repositoryId: 2, behaviour: Superseded);

        await host.RunUntilIdleAsync();

        Assert.Equal(["apply 10/1", "apply 10/2"], host.Handler.Log);
        Assert.Empty(host.Handler.Completed);
        Assert.Equal(0, host.State.GetTotalPendingCount());
    }

    [Fact]
    public async Task A_notification_that_arrives_during_a_completion_gets_a_completion_of_its_own()
    {
        await using var host = new QueueHost();
        var completion = host.Handler.GateNextCompletion();
        host.Enqueue(context: 10, repositoryId: 1);

        await host.Queue.StartAsync(CancellationToken.None);
        await completion.Entered.WaitAsync(Timeout);
        host.Enqueue(context: 10, repositoryId: 2);
        completion.Release();
        await host.Queue.WaitForPendingAsync(QueueHost.WorkspaceId, CancellationToken.None).WaitAsync(Timeout);

        Assert.Equal(["apply 10/1", "complete 10: 1", "apply 10/2", "complete 10: 2"], host.Handler.Log);
    }

    [Fact]
    public async Task The_maximum_batch_size_forces_a_completion()
    {
        await using var host = new QueueHost();
        for (var repositoryId = 1; repositoryId <= WorkerSyncNotificationQueue.MaxBatchSize + 1; repositoryId++)
            host.Enqueue(context: 10, repositoryId);

        await host.RunUntilIdleAsync();

        Assert.Equal(
            [WorkerSyncNotificationQueue.MaxBatchSize, 1],
            host.Handler.Completed.Select(batch => batch.Count));
    }

    [Fact]
    public async Task A_feature_that_is_still_being_created_does_not_hold_up_the_other_contexts()
    {
        await using var host = new QueueHost();
        // Context 20 is a Feature in Create. Its first notification used to be held inside the reader until Create
        // had finished (136 s in the logs of 2026-11-24), with everything else queued behind it.
        var finalization = host.Handler.Finalizing(context: 20);
        host.Enqueue(context: 20, repositoryId: 1);
        host.Enqueue(context: 10, repositoryId: 2);
        host.Enqueue(context: 10, repositoryId: 3);
        host.Enqueue(context: 20, repositoryId: 4);

        await host.Queue.StartAsync(CancellationToken.None);
        await Wait.UntilAsync(() => host.Handler.Log.Contains("complete 10: 2,3"));

        // Context 10 is stored and recomputed; the two notifications of the Feature are still pending.
        Assert.Equal(
            ["apply 20/1", "apply 10/2", "apply 10/3", "apply 20/4", "complete 10: 2,3"],
            host.Handler.Log);
        Assert.Equal(2, host.State.GetPendingCountForWorkspace(QueueHost.WorkspaceId));

        finalization.SetResult(true);
        await host.Queue.WaitForPendingAsync(QueueHost.WorkspaceId, CancellationToken.None).WaitAsync(Timeout);

        Assert.Equal(
            ["apply 20/1", "apply 20/4", "complete 20: 1,4"],
            host.Handler.Log.Skip(5));
    }

    [Fact]
    public async Task A_notification_behind_a_held_one_keeps_its_place_when_the_feature_has_finished_meanwhile()
    {
        await using var host = new QueueHost();
        // Only the first apply sees the Feature in Create: by the second it has finished, but the first notification
        // is still set aside. Applying the second at once would store a newer snapshot before an older one.
        var finalization = host.Handler.Finalizing(context: 20, applies: 1);
        host.Enqueue(context: 20, repositoryId: 1);
        host.Enqueue(context: 20, repositoryId: 2);
        host.Enqueue(context: 10, repositoryId: 3);

        await host.Queue.StartAsync(CancellationToken.None);
        await Wait.UntilAsync(() => host.Handler.Log.Contains("complete 10: 3"));
        Assert.DoesNotContain(host.Handler.Completed, batch => batch[0].ContextId.Value == 20);

        finalization.SetResult(true);
        await host.Queue.WaitForPendingAsync(QueueHost.WorkspaceId, CancellationToken.None).WaitAsync(Timeout);

        Assert.Equal("complete 20: 1,2", host.Handler.Log[^1]);
    }

    [Fact]
    public async Task Held_notifications_are_applied_when_the_wait_for_the_feature_ran_out()
    {
        await using var host = new QueueHost();
        // The Feature never finishes: every apply still sees it in Create.
        var finalization = host.Handler.Finalizing(context: 20);
        host.Enqueue(context: 20, repositoryId: 1);
        host.Enqueue(context: 20, repositoryId: 2);

        await host.Queue.StartAsync(CancellationToken.None);
        await Wait.UntilAsync(() => host.Handler.Log.Count == 2);
        Assert.Empty(host.Handler.Completed);

        // The handler reports false when its wait ran out; the Feature is still in Create when they are applied.
        host.Handler.Finalizing(context: 20);
        finalization.SetResult(false);
        await host.Queue.WaitForPendingAsync(QueueHost.WorkspaceId, CancellationToken.None).WaitAsync(Timeout);

        Assert.Equal("complete 20: 1,2", host.Handler.Log[^1]);
    }

    [Fact]
    public async Task A_feature_that_is_repaired_again_takes_its_held_notifications_back()
    {
        await using var host = new QueueHost();
        var create = host.Handler.Finalizing(context: 20, applies: 1);
        host.Enqueue(context: 20, repositoryId: 1);
        await host.Queue.StartAsync(CancellationToken.None);
        await Wait.UntilAsync(() => host.Handler.Log.Count == 1);

        // Create ends and a Repair begins before the held notification is applied.
        var repair = host.Handler.Finalizing(context: 20);
        create.SetResult(true);
        await Wait.UntilAsync(() => host.Handler.Log.Count == 2);
        Assert.Empty(host.Handler.Completed);
        Assert.Equal(1, host.State.GetPendingCountForWorkspace(QueueHost.WorkspaceId));

        repair.SetResult(true);
        await host.Queue.WaitForPendingAsync(QueueHost.WorkspaceId, CancellationToken.None).WaitAsync(Timeout);

        Assert.Equal("complete 20: 1", host.Handler.Log[^1]);
    }

    [Fact]
    public async Task Stopping_releases_the_notifications_held_for_a_feature()
    {
        await using var host = new QueueHost();
        host.Handler.Finalizing(context: 20);
        host.Enqueue(context: 20, repositoryId: 1);
        var barrier = host.Queue.WaitForPendingAsync(QueueHost.WorkspaceId, CancellationToken.None);

        await host.Queue.StartAsync(CancellationToken.None);
        await Wait.UntilAsync(() => host.Handler.Log.Count == 1);
        Assert.False(barrier.IsCompleted);

        await host.Queue.StopAsync(CancellationToken.None).WaitAsync(Timeout);
        await barrier.WaitAsync(Timeout);

        Assert.Equal(0, host.State.GetTotalPendingCount());
    }

    [Fact]
    public void The_time_a_notification_was_held_for_its_feature_is_not_queue_wait()
    {
        var second = System.Diagnostics.Stopwatch.Frequency;

        // Arrived at 0, applied at 100 s, of which 97 s were spent set aside for Create Feature.
        Assert.Equal(TimeSpan.FromSeconds(3), WorkerSyncNotificationQueue.QueueWait(0, 100 * second, 97 * second));
        Assert.Equal(TimeSpan.FromSeconds(100), WorkerSyncNotificationQueue.QueueWait(0, 100 * second, 0));
    }

    [Fact]
    public async Task Stopping_with_an_open_batch_releases_the_waiters()
    {
        await using var host = new QueueHost();
        var secondWrite = host.Handler.GateStateWriteOf(repositoryId: 2);
        host.Enqueue(context: 10, repositoryId: 1);
        host.Enqueue(context: 10, repositoryId: 2);
        var barrier = host.Queue.WaitForPendingAsync(QueueHost.WorkspaceId, CancellationToken.None);

        await host.Queue.StartAsync(CancellationToken.None);
        await secondWrite.Entered.WaitAsync(Timeout);
        Assert.False(barrier.IsCompleted);

        var stop = host.Queue.StopAsync(CancellationToken.None);
        await barrier.WaitAsync(Timeout);
        secondWrite.Release();
        await stop.WaitAsync(Timeout);

        Assert.Equal(0, host.State.GetTotalPendingCount());
    }

    private sealed class QueueHost : IAsyncDisposable
    {
        public const int WorkspaceId = 1;

        private readonly ServiceProvider _provider;

        public QueueHost()
        {
            var services = new ServiceCollection();
            services.AddSingleton<ISyncNotificationHandler>(Handler);
            _provider = services.BuildServiceProvider();
            Queue = new WorkerSyncNotificationQueue(
                _provider.GetRequiredService<IServiceScopeFactory>(),
                State,
                NullLogger<WorkerSyncNotificationQueue>.Instance);
        }

        public FakeSyncNotificationHandler Handler { get; } = new();
        public AppBackgroundWorkStateService State { get; } = new();
        public WorkerSyncNotificationQueue Queue { get; }

        public void Enqueue(int context, int repositoryId, string behaviour = "") =>
            Queue.Enqueue(new RepositorySyncNotification
            {
                WorkspaceId = WorkspaceId,
                RepositoryId = repositoryId,
                WorkspaceFeatureContextId = context,
                Branch = behaviour,
            });

        /// <summary>Starts the reader and waits until everything queued so far has been released.</summary>
        public async Task RunUntilIdleAsync()
        {
            var idle = Queue.WaitForPendingAsync(WorkspaceId, CancellationToken.None);
            await Queue.StartAsync(CancellationToken.None);
            await idle.WaitAsync(Timeout);
        }

        public async ValueTask DisposeAsync()
        {
            await Queue.StopAsync(CancellationToken.None);
            Queue.Dispose();
            await _provider.DisposeAsync();
        }
    }

    /// <summary>A point the reader stops at until the test releases it.</summary>
    private sealed class Gate
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Entered => _entered.Task;

        public void Release() => _released.TrySetResult();

        public Task PassAsync()
        {
            _entered.TrySetResult();
            return _released.Task;
        }
    }

    /// <summary>
    /// Stands in for <see cref="SyncCommandHandler"/>: the notification's context is its
    /// <see cref="RepositorySyncNotification.WorkspaceFeatureContextId"/> and its <c>Branch</c> names what happens to it.
    /// </summary>
    private sealed class FakeSyncNotificationHandler : ISyncNotificationHandler
    {
        private readonly object _lock = new();
        private readonly List<string> _log = [];
        private readonly List<IReadOnlyList<AppliedSync>> _completed = [];
        private readonly List<long?> _receivedAt = [];
        private Gate? _completionGate;
        private Exception? _completionFailure;
        private (int? Context, int? RepositoryId, Gate Gate)? _stateWriteGate;
        private readonly Dictionary<int, (TaskCompletionSource<bool> Finalization, int Applies)> _finalizing = [];

        public IReadOnlyList<string> Log { get { lock (_lock) return _log.ToList(); } }
        public IReadOnlyList<IReadOnlyList<AppliedSync>> Completed { get { lock (_lock) return _completed.ToList(); } }
        public IReadOnlyList<long?> ReceivedAt { get { lock (_lock) return _receivedAt.ToList(); } }

        public Gate GateNextCompletion() => _completionGate = new Gate();

        public void FailNextCompletion(Exception failure) => _completionFailure = failure;

        public Gate GateStateWriteOf(int? context = null, int? repositoryId = null)
        {
            var gate = new Gate();
            _stateWriteGate = (context, repositoryId, gate);
            return gate;
        }

        /// <summary>
        /// The Feature of <paramref name="context"/> is still being created: each of the next
        /// <paramref name="applies"/> applies of that context is told so, with a wait the test ends through the result.
        /// </summary>
        public TaskCompletionSource<bool> Finalizing(int context, int applies = int.MaxValue)
        {
            var finalization = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_lock)
                _finalizing[context] = (finalization, applies);
            return finalization;
        }

        private Task<bool>? NextFinalization(int context)
        {
            lock (_lock)
            {
                if (!_finalizing.TryGetValue(context, out var finalizing) || finalizing.Finalization.Task.IsCompleted)
                    return null;
                if (finalizing.Applies <= 0)
                    return null;
                _finalizing[context] = (finalizing.Finalization, finalizing.Applies - 1);
                return finalizing.Finalization.Task;
            }
        }

        public async Task ApplyAsync(RepositorySyncNotification n, long? receivedAt, ISyncApplyObserver observer)
        {
            var context = new WorkspaceFeatureContextId(n.WorkspaceFeatureContextId!.Value);
            lock (_lock)
            {
                _log.Add($"apply {context.Value}/{n.RepositoryId}");
                _receivedAt.Add(receivedAt);
            }

            if (n.Branch == Skipped)
                return;

            if (!await observer.ContextResolvedAsync(n.WorkspaceId, context, NextFinalization(context.Value)))
                return;

            if (_stateWriteGate is { } gated
                && (gated.Context is null || gated.Context == context.Value)
                && (gated.RepositoryId is null || gated.RepositoryId == n.RepositoryId))
            {
                _stateWriteGate = null;
                await gated.Gate.PassAsync();
            }

            if (n.Branch == Superseded)
                return;
            if (n.Branch == ThrowsBeforeWrite)
                throw new InvalidOperationException("state write failed");

            observer.StateWritten(new AppliedSync(
                n.WorkspaceId, context, IsSpecialWorkspace: false, n.RepositoryId, NeedsRecompute: n.Branch != RefsOnly));

            if (n.Branch == ThrowsAfterWrite)
                throw new InvalidOperationException("tag refresh failed");
        }

        public async Task CompleteAsync(IReadOnlyList<AppliedSync> items)
        {
            lock (_lock)
            {
                _log.Add($"complete {items[0].ContextId.Value}: {string.Join(",", items.Select(i => i.RepositoryId))}");
                _completed.Add(items);
            }

            if (Interlocked.Exchange(ref _completionGate, null) is { } gate)
                await gate.PassAsync();
            if (Interlocked.Exchange(ref _completionFailure, null) is { } failure)
                throw failure;
        }
    }
}
