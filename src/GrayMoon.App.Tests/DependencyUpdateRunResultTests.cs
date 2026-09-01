using GrayMoon.Application;

namespace GrayMoon.App.Tests;

public sealed class DependencyUpdateRunResultTests
{
    [Fact]
    public void Failed_update_does_not_chain_push()
    {
        var result = DependencyUpdateRunResult.Failed();

        Assert.False(result.Success);
        Assert.Empty(result.SyncedRepoIds);
        Assert.False(result.ShouldChainPush(pushRequested: true));
        Assert.False(result.ShouldChainPush(pushRequested: false));
    }

    [Fact]
    public void Successful_update_chains_push_only_when_requested()
    {
        var result = DependencyUpdateRunResult.Ok(new HashSet<int> { 7 });

        Assert.True(result.Success);
        Assert.Equal([7], result.SyncedRepoIds);
        Assert.True(result.ShouldChainPush(pushRequested: true));
        Assert.False(result.ShouldChainPush(pushRequested: false));
    }

    [Fact]
    public void Successful_update_with_nothing_synced_still_allows_push()
    {
        var result = DependencyUpdateRunResult.Ok();

        Assert.True(result.Success);
        Assert.True(result.ShouldChainPush(pushRequested: true));
    }
}
