using System.Net;
using System.Net.Http.Headers;
using GrayMoon.App.Models;
using GrayMoon.App.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace GrayMoon.App.Tests;

public sealed class GitHubServiceETagCacheTests
{
    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Queue<(HttpStatusCode Status, string? Body, string? ETag)> _responses = new();

        public List<HttpRequestMessage> Requests { get; } = new();

        public void Enqueue(HttpStatusCode status, string? body, string? etag) => _responses.Enqueue((status, body, etag));

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var (status, body, etag) = _responses.Dequeue();
            var response = new HttpResponseMessage(status);
            if (body != null)
                response.Content = new StringContent(body);
            if (etag != null)
                response.Headers.ETag = new EntityTagHeaderValue(etag);
            return Task.FromResult(response);
        }
    }

    private static (GitHubService Service, ScriptedHandler Handler) CreateService()
    {
        var handler = new ScriptedHandler();
        var httpClient = new HttpClient(handler);
        var configuration = new ConfigurationBuilder().Build();
        var service = new GitHubService(httpClient, configuration, new GitHubRateLimitTracker(), new GitHubETagCache(), new FakeGitHubApiUsageRecorder(), NullLogger<GitHubService>.Instance);
        return (service, handler);
    }

    private static Connector CreateConnector() => new()
    {
        ConnectorId = 1,
        ConnectorName = "GitHub",
        ConnectorType = ConnectorType.GitHub,
        ApiBaseUrl = "https://api.github.com/",
        UserToken = "test-token"
    };

    [Fact]
    public async Task GetWorkflowRunJobsAsync_SecondPoll_SendsIfNoneMatchWithPriorETag()
    {
        var (service, handler) = CreateService();
        var connector = CreateConnector();

        handler.Enqueue(HttpStatusCode.OK, """{"total_count":1,"jobs":[{"id":1,"name":"build","status":"in_progress"}]}""", "\"abc123\"");
        handler.Enqueue(HttpStatusCode.NotModified, null, "\"abc123\"");

        var first = await service.GetWorkflowRunJobsAsync(connector, "acme", "widgets", 999);
        var second = await service.GetWorkflowRunJobsAsync(connector, "acme", "widgets", 999);

        Assert.Equal(2, handler.Requests.Count);
        Assert.Null(handler.Requests[0].Headers.IfNoneMatch.FirstOrDefault());
        Assert.Equal("\"abc123\"", handler.Requests[1].Headers.IfNoneMatch.FirstOrDefault()?.Tag);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(first!.Jobs[0].Name, second!.Jobs[0].Name);
    }

    [Fact]
    public async Task GetWorkflowRunJobsAsync_NotModified_ReturnsCachedBodyNotEmptyResult()
    {
        var (service, handler) = CreateService();
        var connector = CreateConnector();

        handler.Enqueue(HttpStatusCode.OK, """{"total_count":2,"jobs":[{"id":1,"name":"build","status":"completed"},{"id":2,"name":"test","status":"completed"}]}""", "\"etag-1\"");
        handler.Enqueue(HttpStatusCode.NotModified, null, "\"etag-1\"");

        await service.GetWorkflowRunJobsAsync(connector, "acme", "widgets", 42);
        var result = await service.GetWorkflowRunJobsAsync(connector, "acme", "widgets", 42);

        Assert.NotNull(result);
        Assert.Equal(2, result!.Jobs.Count);
        Assert.Equal("build", result.Jobs[0].Name);
        Assert.Equal("test", result.Jobs[1].Name);
    }

    [Fact]
    public async Task GetWorkflowRunJobsAsync_DifferentRunIds_DoNotShareETagCacheEntry()
    {
        var (service, handler) = CreateService();
        var connector = CreateConnector();

        handler.Enqueue(HttpStatusCode.OK, """{"total_count":0,"jobs":[]}""", "\"run-1-etag\"");
        handler.Enqueue(HttpStatusCode.OK, """{"total_count":0,"jobs":[]}""", "\"run-2-etag\"");

        await service.GetWorkflowRunJobsAsync(connector, "acme", "widgets", 1);
        await service.GetWorkflowRunJobsAsync(connector, "acme", "widgets", 2);

        Assert.Equal(2, handler.Requests.Count);
        Assert.All(handler.Requests, r => Assert.Null(r.Headers.IfNoneMatch.FirstOrDefault()));
    }
}
