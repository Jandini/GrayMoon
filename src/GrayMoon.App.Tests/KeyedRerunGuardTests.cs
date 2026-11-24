using GrayMoon.App.Components.Shared;

namespace GrayMoon.App.Tests;

/// <summary>
/// The Actions page starts a loop of GitHub polls for a repository after each of its hook syncs. Every sync started
/// its own loop, so a push (several syncs) polled the same repository several times side by side.
/// </summary>
public sealed class KeyedRerunGuardTests
{
    [Fact]
    public async Task Requests_during_a_run_do_not_start_a_second_run_and_ask_for_one_more()
    {
        var guard = new KeyedRerunGuard<int>();
        var runs = 0;
        var active = 0;
        var maxActive = 0;
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task Work()
        {
            runs++;
            maxActive = Math.Max(maxActive, ++active);
            firstEntered.TrySetResult();
            await release.Task;
            active--;
        }

        var first = guard.RunAsync(7, Work);
        await firstEntered.Task;
        var second = guard.RunAsync(7, Work);
        var third = guard.RunAsync(7, Work);

        // The later requests return at once: the running loop takes them over.
        Assert.True(second.IsCompletedSuccessfully);
        Assert.True(third.IsCompletedSuccessfully);
        Assert.Equal(1, runs);

        release.SetResult();
        await first;

        Assert.Equal(2, runs);
        Assert.Equal(1, maxActive);
    }

    [Fact]
    public async Task Another_key_runs_at_the_same_time_and_a_finished_key_can_run_again()
    {
        var guard = new KeyedRerunGuard<int>();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runsByKey = new Dictionary<int, int>();

        var first = guard.RunAsync(1, async () =>
        {
            runsByKey[1] = runsByKey.GetValueOrDefault(1) + 1;
            await release.Task;
        });
        await guard.RunAsync(2, () =>
        {
            runsByKey[2] = runsByKey.GetValueOrDefault(2) + 1;
            return Task.CompletedTask;
        });
        release.SetResult();
        await first;
        await guard.RunAsync(1, () =>
        {
            runsByKey[1]++;
            return Task.CompletedTask;
        });

        Assert.Equal(2, runsByKey[1]);
        Assert.Equal(1, runsByKey[2]);
    }

    [Fact]
    public async Task A_failed_run_frees_its_key()
    {
        var guard = new KeyedRerunGuard<int>();

        await Assert.ThrowsAsync<InvalidOperationException>(() => guard.RunAsync(1, () => throw new InvalidOperationException("poll failed")));

        var ran = false;
        await guard.RunAsync(1, () =>
        {
            ran = true;
            return Task.CompletedTask;
        });
        Assert.True(ran);
    }
}
