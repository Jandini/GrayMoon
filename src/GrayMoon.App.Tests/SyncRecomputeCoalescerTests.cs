using GrayMoon.App.Services.Workspaces;

namespace GrayMoon.App.Tests;

public sealed class SyncRecomputeCoalescerTests
{
    [Fact]
    public async Task A_burst_shares_runs_and_each_caller_sees_a_run_that_started_after_it_arrived()
    {
        var sut = new SyncRecomputeCoalescer();
        var started = 0;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstRunning = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task Work()
        {
            var n = Interlocked.Increment(ref started);
            if (n == 1)
            {
                firstRunning.SetResult();
                await gate.Task;
            }
            else
            {
                await Task.Delay(10);
            }
        }

        var first = sut.RunAsync("k", Work);
        await firstRunning.Task;

        var late = Enumerable.Range(0, 10).Select(_ => sut.RunAsync("k", Work)).ToArray();
        await Task.Delay(50);
        Assert.Equal(1, Volatile.Read(ref started));
        Assert.All(late, t => Assert.False(t.IsCompleted));

        gate.SetResult();
        await Task.WhenAll(late.Append(first));

        Assert.Equal(2, started);
    }

    [Fact]
    public async Task Keys_are_independent_and_a_failure_reaches_only_its_batch()
    {
        var sut = new SyncRecomputeCoalescer();

        await sut.RunAsync("a", () => Task.CompletedTask);
        await Assert.ThrowsAsync<InvalidOperationException>(() => sut.RunAsync("b", () => throw new InvalidOperationException("x")));
        await sut.RunAsync("b", () => Task.CompletedTask);
    }
}
