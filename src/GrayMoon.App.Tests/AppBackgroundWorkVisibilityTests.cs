using GrayMoon.App.Components.Shared;
using GrayMoon.App.Services.Worker;

namespace GrayMoon.App.Tests;

/// <summary>
/// The App's pending repository updates are visible: the state service counts them, the Repositories header and the
/// Worker badge show them, and a wait on the sync barrier reports the remaining count.
/// </summary>
public sealed class AppBackgroundWorkVisibilityTests
{
    [Fact]
    public void State_counts_per_Workspace_and_in_total_and_returns_to_zero()
    {
        var state = new AppBackgroundWorkStateService();
        state.Increment(1);
        state.Increment(1);
        state.Increment(2);

        Assert.Equal(2, state.GetPendingCountForWorkspace(1));
        Assert.Equal(1, state.GetPendingCountForWorkspace(2));
        Assert.Equal(3, state.GetTotalPendingCount());

        state.Decrement(1);
        state.Decrement(1);
        state.Decrement(2);

        Assert.Equal(0, state.GetPendingCountForWorkspace(1));
        Assert.Equal(0, state.GetTotalPendingCount());
    }

    [Fact]
    public void State_never_goes_below_zero()
    {
        var state = new AppBackgroundWorkStateService();
        var changes = 0;
        state.OnStateChanged((_, _) => changes++);

        state.Decrement(1);

        Assert.Equal(0, state.GetPendingCountForWorkspace(1));
        Assert.Equal(0, state.GetTotalPendingCount());
        Assert.Equal(0, changes);
    }

    [Fact]
    public void State_raises_a_change_for_every_update_and_not_for_a_removed_handler()
    {
        var state = new AppBackgroundWorkStateService();
        var changes = 0;
        EventHandler handler = (_, _) => changes++;
        state.OnStateChanged(handler);

        state.Increment(1);
        state.Decrement(1);
        Assert.Equal(2, changes);

        state.RemoveStateChanged(handler);
        state.Increment(1);
        Assert.Equal(2, changes);
    }

    [Theory]
    [InlineData(3, 5, "Worker is completing 3 tasks")]
    [InlineData(1, 0, "Worker is completing 1 task")]
    [InlineData(0, 5, "Applying 5 repository updates")]
    [InlineData(0, 1, "Applying 1 repository update")]
    [InlineData(0, 0, null)]
    public void Header_shows_one_line_and_the_Worker_wins(int workerTasks, int appTasks, string? expected)
    {
        Assert.Equal(expected, WorkspaceRepositoriesHeader.BackgroundWorkLine(workerTasks, appTasks));
    }

    [Fact]
    public void Header_and_page_never_disable_a_button_on_the_App_count()
    {
        var components = Path.Combine(SourceTree.AppProjectDirectory, "Components");
        var header = File.ReadAllText(Path.Combine(components, "Shared", "WorkspaceRepositoriesHeader.razor"));
        var disableRules = header.Split('\n').Where(line => line.Contains("Disabled", StringComparison.Ordinal));
        Assert.DoesNotContain(disableRules, line => line.Contains("AppTasksPendingCount", StringComparison.Ordinal));
    }

    [Fact]
    public void Badge_and_page_remove_their_App_background_work_handlers()
    {
        var components = Path.Combine(SourceTree.AppProjectDirectory, "Components");
        var badge = File.ReadAllText(Path.Combine(components, "Shared", "WorkerStatusBadge.razor"));
        Assert.Contains("AppBackgroundWork.OnStateChanged(OnQueueStateChanged)", badge);
        Assert.Contains("AppBackgroundWork.RemoveStateChanged(OnQueueStateChanged)", badge);

        var page = File.ReadAllText(Path.Combine(components, "Pages", "WorkspaceRepositories.razor.cs"));
        Assert.Contains("AppBackgroundWork.OnStateChanged(OnQueueStateChanged)", page);
        Assert.Contains("AppBackgroundWork.RemoveStateChanged(OnQueueStateChanged)", page);
    }

    [Fact]
    public void Badge_title_names_the_App_work_only_while_there_is_some()
    {
        var busy = WorkerStatusText.Title(false, false, WorkerConnectionState.Online, "1.0.0", 0, false, appPendingCount: 4);
        Assert.Contains("GrayMoon is applying 4 repository updates", busy);

        var idle = WorkerStatusText.Title(false, false, WorkerConnectionState.Online, "1.0.0", 0, false);
        Assert.DoesNotContain("applying", idle);
    }

    [Fact]
    public async Task Barrier_wait_reports_the_remaining_count_and_removes_its_handler()
    {
        var state = new AppBackgroundWorkStateService();
        var barrier = new ManualBarrier();
        var messages = new List<string>();
        state.Increment(7);
        state.Increment(7);
        state.Increment(8);

        var wait = PendingSyncNotificationWait.WaitAsync(barrier, state, 7, messages.Add, CancellationToken.None);
        state.Decrement(7);
        state.Decrement(8);
        state.Decrement(7);
        barrier.Release();
        await wait;

        Assert.Equal(
            ["Applying 2 pending repository changes...", "Applying 1 pending repository change...", "Applying 1 pending repository change..."],
            messages);

        state.Increment(7);
        Assert.Equal(3, messages.Count);
    }

    [Fact]
    public async Task Barrier_wait_with_nothing_pending_keeps_the_plain_message()
    {
        var messages = new List<string>();
        var barrier = new ManualBarrier();
        barrier.Release();

        await PendingSyncNotificationWait.WaitAsync(barrier, new AppBackgroundWorkStateService(), 7, messages.Add, CancellationToken.None);

        Assert.Equal(["Applying pending repository changes..."], messages);
    }

    [Fact]
    public async Task Cancelled_barrier_wait_removes_its_handler()
    {
        var state = new AppBackgroundWorkStateService();
        var messages = new List<string>();
        state.Increment(7);
        using var cts = new CancellationTokenSource();

        var wait = PendingSyncNotificationWait.WaitAsync(new ManualBarrier(), state, 7, messages.Add, cts.Token);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);

        state.Increment(7);
        Assert.Single(messages);
    }

    private sealed class ManualBarrier : IWorkerSyncNotificationBarrier
    {
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release() => _released.TrySetResult();

        public Task WaitForPendingAsync(int workspaceId, CancellationToken cancellationToken) =>
            _released.Task.WaitAsync(cancellationToken);
    }
}
