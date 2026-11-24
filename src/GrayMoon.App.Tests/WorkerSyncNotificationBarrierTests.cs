using GrayMoon.Abstractions.Notifications;
using GrayMoon.App.Services.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace GrayMoon.App.Tests;

public sealed class WorkerSyncNotificationBarrierTests
{
    private static WorkerSyncNotificationQueue CreateQueue(AppBackgroundWorkStateService? state = null) =>
        new(new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            state ?? new AppBackgroundWorkStateService(),
            NullLogger<WorkerSyncNotificationQueue>.Instance);

    [Fact]
    public async Task A_queued_notification_is_counted_as_App_background_work_until_it_is_applied()
    {
        var state = new AppBackgroundWorkStateService();
        var queue = CreateQueue(state);
        queue.Enqueue(Notification(1));
        queue.Enqueue(Notification(1));
        queue.Enqueue(Notification(2));

        Assert.Equal(2, state.GetPendingCountForWorkspace(1));
        Assert.Equal(1, state.GetPendingCountForWorkspace(2));
        Assert.Equal(3, state.GetTotalPendingCount());

        // No SyncCommandHandler is registered, so every item fails in the handler: the failure path must still end at zero.
        var wait = Task.WhenAll(
            queue.WaitForPendingAsync(1, CancellationToken.None),
            queue.WaitForPendingAsync(2, CancellationToken.None));
        using var cts = new CancellationTokenSource();
        await queue.StartAsync(cts.Token);
        await wait.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(0, state.GetTotalPendingCount());
        Assert.Equal(0, state.GetPendingCountForWorkspace(1));

        await queue.StopAsync(CancellationToken.None);
    }

    private static RepositorySyncNotification Notification(int workspaceId) =>
        new() { WorkspaceId = workspaceId, RepositoryId = 1 };

    [Fact]
    public async Task Waiting_with_nothing_queued_completes_immediately()
    {
        var queue = CreateQueue();

        await queue.WaitForPendingAsync(1, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task A_notification_that_is_still_queued_holds_the_barrier_until_it_is_applied()
    {
        var queue = CreateQueue();
        queue.Enqueue(Notification(1));

        var wait = queue.WaitForPendingAsync(1, CancellationToken.None);
        await Task.Delay(50);
        Assert.False(wait.IsCompleted);

        // The queue is not started here, so the notification is never applied until the service runs it.
        using var cts = new CancellationTokenSource();
        await queue.StartAsync(cts.Token);
        await wait.WaitAsync(TimeSpan.FromSeconds(5));

        await queue.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task A_notification_for_another_workspace_does_not_hold_the_barrier()
    {
        var queue = CreateQueue();
        queue.Enqueue(Notification(2));

        await queue.WaitForPendingAsync(1, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task A_cancelled_wait_stops_waiting_without_losing_the_pending_notification()
    {
        var queue = CreateQueue();
        queue.Enqueue(Notification(1));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queue.WaitForPendingAsync(1, cts.Token));
    }
}
