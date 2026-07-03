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
        var first = loader.DebounceSearchAsync(async () => { runs++; await Task.CompletedTask; }, 200);
        await Task.Delay(50);
        var second = loader.DebounceSearchAsync(async () => { runs++; await Task.CompletedTask; }, 200);
        await Task.WhenAll(first, second);
        Assert.Equal(1, runs);
    }
}
