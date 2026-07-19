using System.Net;
using System.Text;
using System.Text.Json;
using GrayMoon.App.Models;
using GrayMoon.App.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace GrayMoon.App.Tests;

public sealed class GitHubServiceWorkflowRunTests
{
    private sealed class RecordingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastRequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            LastRequestBody = request.Content == null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }
    }

    private static (GitHubService Service, RecordingHandler Handler) CreateService()
    {
        var handler = new RecordingHandler();
        var httpClient = new HttpClient(handler);
        var configuration = new ConfigurationBuilder().Build();
        var service = new GitHubService(httpClient, configuration, new GitHubRateLimitTracker(), NullLogger<GitHubService>.Instance);
        return (service, handler);
    }

    private static Connector CreateConnector() => new()
    {
        ConnectorName = "GitHub",
        ConnectorType = ConnectorType.GitHub,
        ApiBaseUrl = "https://api.github.com/",
        UserToken = "test-token"
    };

    [Fact]
    public async Task DispatchWorkflowAsync_PostsToDispatchesEndpoint_WithBranchAsRef()
    {
        var (service, handler) = CreateService();

        await service.DispatchWorkflowAsync(CreateConnector(), "acme", "widgets", 123, "feature/my-branch");

        Assert.NotNull(handler.LastRequest);
        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.Equal(
            "https://api.github.com/repos/acme/widgets/actions/workflows/123/dispatches",
            handler.LastRequest.RequestUri!.ToString());

        Assert.NotNull(handler.LastRequestBody);
        using var json = JsonDocument.Parse(handler.LastRequestBody!);
        Assert.Equal("feature/my-branch", json.RootElement.GetProperty("ref").GetString());
    }

    [Fact]
    public async Task RerunWorkflowRunAsync_PostsToRerunEndpoint_ForTheExistingRunId()
    {
        var (service, handler) = CreateService();

        await service.RerunWorkflowRunAsync(CreateConnector(), "acme", "widgets", 456);

        Assert.NotNull(handler.LastRequest);
        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.Equal(
            "https://api.github.com/repos/acme/widgets/actions/runs/456/rerun",
            handler.LastRequest.RequestUri!.ToString());
    }

    [Fact]
    public async Task DispatchAndRerun_HitDifferentEndpoints()
    {
        var (service, handler) = CreateService();
        var connector = CreateConnector();

        await service.DispatchWorkflowAsync(connector, "acme", "widgets", 123, "main");
        var dispatchUri = handler.LastRequest!.RequestUri!.ToString();

        await service.RerunWorkflowRunAsync(connector, "acme", "widgets", 456);
        var rerunUri = handler.LastRequest!.RequestUri!.ToString();

        Assert.NotEqual(dispatchUri, rerunUri);
        Assert.Contains("/dispatches", dispatchUri);
        Assert.Contains("/rerun", rerunUri);
        Assert.DoesNotContain("/rerun", dispatchUri);
        Assert.DoesNotContain("/dispatches", rerunUri);
    }
}
