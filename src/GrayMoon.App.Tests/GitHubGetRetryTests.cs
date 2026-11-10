using GrayMoon.App.Models;
using GrayMoon.App.Services.GitHub;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace GrayMoon.App.Tests;

/// <summary>
/// Review 2026-11-10 (GitHubService.Http): a GET that hits the per-request timeout was retried three times with
/// backoff, so one hung endpoint stretched a refresh by minutes. It now costs exactly one extra attempt, with
/// no backoff, and a cancel by the caller is never retried.
/// </summary>
public sealed class GitHubGetRetryTests
{
    [Fact]
    public async Task Get_that_times_out_is_retried_once_and_then_fails()
    {
        var handler = new TimingOutHandler();
        var service = CreateService(handler);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.GetRepositoriesAsync(CreateConnector(), cancellationToken: CancellationToken.None));

        Assert.Equal(2, handler.Attempts);
    }

    [Fact]
    public async Task Get_cancelled_by_the_caller_is_not_retried()
    {
        using var cts = new CancellationTokenSource();
        var handler = new TimingOutHandler { OnAttempt = cts.Cancel };
        var service = CreateService(handler);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.GetRepositoriesAsync(CreateConnector(), cancellationToken: cts.Token));

        Assert.Equal(1, handler.Attempts);
    }

    private static GitHubService CreateService(HttpMessageHandler handler) => new(
        new HttpClient(handler),
        new ConfigurationBuilder().Build(),
        new GitHubRateLimitTracker(),
        new GitHubETagCache(),
        new FakeGitHubApiUsageRecorder(),
        NullLogger<GitHubService>.Instance);

    private static Connector CreateConnector() => new()
    {
        ConnectorId = 1,
        ConnectorName = "github",
        ConnectorType = ConnectorType.GitHub,
        ApiBaseUrl = "https://hung.example/",
        UserToken = "test-token",
        IsActive = true
    };

    /// <summary>Fails every request the way HttpClient reports its own per-request timeout.</summary>
    private sealed class TimingOutHandler : HttpMessageHandler
    {
        private int _attempts;

        public int Attempts => Volatile.Read(ref _attempts);
        public Action? OnAttempt { get; init; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _attempts);
            OnAttempt?.Invoke();
            throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout elapsing.", new TimeoutException());
        }
    }
}
