using GrayMoon.App.Components.Shared;

namespace GrayMoon.App.Tests;

public class DebouncedQueryLoaderTests
{
    [Fact]
    public async Task BeginQueryCycle_increments_generation()
    {
        using var loader = new DebouncedQueryLoader();
        loader.BeginQueryCycle(out var gen1);
        loader.BeginQueryCycle(out var gen2);
        Assert.Equal(1, gen1);
        Assert.Equal(2, gen2);
    }

    [Fact]
    public async Task DebounceSearchAsync_cancels_prior_delay()
    {
        using var loader = new DebouncedQueryLoader();
        var runs = 0;
        // Second call must start before the first delay can fire; a wall-clock wait here is what
        // flaked on CI when the runner paused longer than the first delay.
        var first = loader.DebounceSearchAsync(async () => { runs++; await Task.CompletedTask; }, delayMs: 30_000);
        var second = loader.DebounceSearchAsync(async () => { runs++; await Task.CompletedTask; }, delayMs: 1);
        await Task.WhenAll(first, second);
        Assert.Equal(1, runs);
    }
}
