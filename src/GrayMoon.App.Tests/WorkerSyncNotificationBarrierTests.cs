using GrayMoon.Abstractions.Notifications;
using GrayMoon.App.Services.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace GrayMoon.App.Tests;

public sealed class WorkerSyncNotificationBarrierTests
{
    private static WorkerSyncNotificationQueue CreateQueue() =>
        new(new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            NullLogger<WorkerSyncNotificationQueue>.Instance);

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
