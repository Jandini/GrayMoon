using GrayMoon.App.Models;
using GrayMoon.App.Services;

namespace GrayMoon.App.Tests;

public sealed class GhaLiveFeedJobsCacheTests
{
    private static GitHubWorkflowJobsResponse MakeResponse(int totalCount) => new() { TotalCount = totalCount };

    [Fact]
    public void TryGet_BeforeAnySet_ReturnsFalse()
    {
        var cache = new GhaLiveFeedJobsCache();

        Assert.False(cache.TryGet("GitHub|acme|widgets|1", out var response));
        Assert.Null(response);
    }

    [Fact]
    public void Set_ThenTryGet_ReturnsSameResponseWithinCoalesceWindow()
    {
        var cache = new GhaLiveFeedJobsCache();
        var response = MakeResponse(3);

        cache.Set("GitHub|acme|widgets|1", response);

        Assert.True(cache.TryGet("GitHub|acme|widgets|1", out var cached));
        Assert.Same(response, cached);
    }

    [Fact]
    public void TryGet_DifferentRunKey_DoesNotReturnOtherRunsEntry()
    {
        var cache = new GhaLiveFeedJobsCache();
        cache.Set("GitHub|acme|widgets|1", MakeResponse(1));

        Assert.False(cache.TryGet("GitHub|acme|widgets|2", out var response));
        Assert.Null(response);
    }

    [Fact]
    public void Set_NullResponse_IsStillCachedAsFoundSoCallerDoesNotRefetch()
    {
        var cache = new GhaLiveFeedJobsCache();

        cache.Set("GitHub|acme|widgets|1", null);

        // A workflow with zero jobs assigned yet legitimately caches a null response - TryGet must return
        // true (found) with a null out value, not report a cache miss, or every poller would keep re-fetching.
        Assert.True(cache.TryGet("GitHub|acme|widgets|1", out var response));
        Assert.Null(response);
    }
}
