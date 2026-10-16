using GrayMoon.App.Services.Jobs;

namespace GrayMoon.App.Tests;

public sealed class WorkspaceOperationBackgroundTests
{
    [Fact]
    public async Task Background_stops_only_the_phase_and_the_operation_goes_on()
    {
        var op = new WorkspaceOperation(7, "create-feature", "/workspaces/7", "Creating feature...");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.False(op.CanRunInBackground);

        var phase = op.RunBackgroundableAsync(async ct =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
        });
        await entered.Task;
        Assert.True(op.CanRunInBackground);

        op.RunInBackground();
        await phase.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(op.CanRunInBackground);
        Assert.Equal(BackgroundJobState.Running, op.State);
        Assert.False(op.CancellationToken.IsCancellationRequested);
    }

    [Fact]
    public async Task Abort_during_the_phase_still_aborts_the_operation()
    {
        var op = new WorkspaceOperation(7, "create-feature", "/workspaces/7", "Creating feature...");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var phase = op.RunBackgroundableAsync(async ct =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
        });
        await entered.Task;
        op.Abort();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => phase.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(BackgroundJobState.Aborted, op.State);
    }

    [Fact]
    public void Background_outside_a_phase_does_nothing()
    {
        var op = new WorkspaceOperation(7, "create-feature", "/workspaces/7", "Creating feature...");

        op.RunInBackground();

        Assert.Equal(BackgroundJobState.Running, op.State);
        Assert.False(op.CancellationToken.IsCancellationRequested);
    }
}
