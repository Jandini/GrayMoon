using GrayMoon.Worker.Services;

namespace GrayMoon.Worker.Tests;

public sealed class RepositoryPassCoalescerTests
{
    private const string Repo = "C:\\repo";

    [Fact]
    public async Task A_single_request_runs_one_pass()
    {
        var passes = 0;
        var coalescer = new RepositoryPassCoalescer(TimeSpan.Zero);

        await coalescer.RunAsync(Repo, _ => { passes++; return Task.CompletedTask; }, CancellationToken.None);

        Assert.Equal(1, passes);
    }

    [Fact]
    public async Task Requests_during_a_pass_run_exactly_one_more_pass_and_never_overlap()
    {
        var passes = 0;
        var running = 0;
        var overlapped = false;
        var firstPassStarted = new TaskCompletionSource();
        var releaseFirstPass = new TaskCompletionSource();
        var coalescer = new RepositoryPassCoalescer(TimeSpan.Zero);

        async Task Pass(CancellationToken ct)
        {
            if (Interlocked.Increment(ref running) > 1)
                overlapped = true;
            var number = Interlocked.Increment(ref passes);
            if (number == 1)
            {
                firstPassStarted.SetResult();
                await releaseFirstPass.Task;
            }

            Interlocked.Decrement(ref running);
        }

        var owner = coalescer.RunAsync(Repo, Pass, CancellationToken.None);
        await firstPassStarted.Task;

        // Ten more requests arrive while the first pass is still running: all of them return at once.
        for (var i = 0; i < 10; i++)
            await coalescer.RunAsync(Repo, Pass, CancellationToken.None);
        Assert.Equal(1, passes);

        releaseFirstPass.SetResult();
        await owner;

        Assert.Equal(2, passes);
        Assert.False(overlapped);
    }

    [Fact]
    public async Task A_request_after_the_burst_starts_a_new_pass()
    {
        var passes = 0;
        var coalescer = new RepositoryPassCoalescer(TimeSpan.Zero);

        await coalescer.RunAsync(Repo, _ => { passes++; return Task.CompletedTask; }, CancellationToken.None);
        await coalescer.RunAsync(Repo, _ => { passes++; return Task.CompletedTask; }, CancellationToken.None);

        Assert.Equal(2, passes);
    }

    [Fact]
    public async Task Different_repositories_do_not_wait_for_each_other()
    {
        var release = new TaskCompletionSource();
        var started = new TaskCompletionSource();
        var coalescer = new RepositoryPassCoalescer(TimeSpan.Zero);

        var first = coalescer.RunAsync(Repo, async _ => { started.SetResult(); await release.Task; }, CancellationToken.None);
        await started.Task;

        var otherRan = false;
        await coalescer.RunAsync("C:\\other", _ => { otherRan = true; return Task.CompletedTask; }, CancellationToken.None);

        Assert.True(otherRan);
        release.SetResult();
        await first;
    }

    [Fact]
    public async Task A_failed_pass_does_not_leave_the_repository_locked()
    {
        var coalescer = new RepositoryPassCoalescer(TimeSpan.Zero);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            coalescer.RunAsync(Repo, _ => throw new InvalidOperationException("boom"), CancellationToken.None));

        var ran = false;
        await coalescer.RunAsync(Repo, _ => { ran = true; return Task.CompletedTask; }, CancellationToken.None);
        Assert.True(ran);
    }

    [Fact]
    public async Task The_debounce_lets_a_burst_finish_before_the_pass_reads_anything()
    {
        var passes = 0;
        var coalescer = new RepositoryPassCoalescer(TimeSpan.FromMilliseconds(150));

        var owner = coalescer.RunAsync(Repo, _ => { passes++; return Task.CompletedTask; }, CancellationToken.None);
        for (var i = 0; i < 5; i++)
            await coalescer.RunAsync(Repo, _ => { passes++; return Task.CompletedTask; }, CancellationToken.None);
        await owner;

        Assert.Equal(1, passes);
    }
}
