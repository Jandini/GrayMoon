using System.Net;
using System.Net.Http.Headers;
using GrayMoon.App.Data;
using GrayMoon.App.Hubs;
using GrayMoon.App.Models;
using GrayMoon.App.Services;
using GrayMoon.App.Services.Policy;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using static GrayMoon.App.Tests.Policy.Characterization.PolicyHarness;

namespace GrayMoon.App.Tests.Policy;

/// <summary>
/// The archive check for GitHub through the real <see cref="GitHubService"/>: the request it sends and how it reads
/// the answer. Only the HTTP transport is scripted. GitHub itself is not called.
/// </summary>
public sealed class GitHubRepositoryArchiveProbeTests
{
    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Queue<(HttpStatusCode Status, string? Body, string? ETag)> _responses = new();

        public List<HttpRequestMessage> Requests { get; } = [];

        public void Enqueue(HttpStatusCode status, string? body = null, string? etag = null) => _responses.Enqueue((status, body, etag));

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

    private static (GitHubRepositoryArchiveProbe Probe, ScriptedHandler Handler) CreateProbe()
    {
        var handler = new ScriptedHandler();
        var service = new GitHubService(
            new HttpClient(handler),
            new ConfigurationBuilder().Build(),
            new GitHubRateLimitTracker(),
            new GitHubETagCache(),
            new FakeGitHubApiUsageRecorder(),
            NullLogger<GitHubService>.Instance);
        return (new GitHubRepositoryArchiveProbe(service), handler);
    }

    private static Connector GitHubConnector() => new()
    {
        ConnectorId = 1,
        ConnectorName = "GitHub",
        ConnectorType = ConnectorType.GitHub,
        ApiBaseUrl = "https://api.github.com/",
        UserToken = "test-token",
    };

    private static Repository RepositoryOf(Connector? connector, string cloneUrl = "https://github.com/acme/widgets.git") => new()
    {
        RepositoryId = 7,
        RepositoryName = "widgets",
        CloneUrl = cloneUrl,
        Connector = connector,
        ConnectorId = connector?.ConnectorId ?? 0,
    };

    // The part of a real "GET /repos/{owner}/{repo}" answer the check reads, with fields around it.
    private static string RepositoryJson(bool archived) =>
        $$"""{"id":1296269,"name":"widgets","full_name":"acme/widgets","private":false,"archived":{{(archived ? "true" : "false")}},"disabled":false,"allow_squash_merge":true,"default_branch":"main"}""";

    [Fact]
    public async Task An_archived_repository_is_reported_as_archived_from_the_repository_endpoint()
    {
        var (probe, handler) = CreateProbe();
        handler.Enqueue(HttpStatusCode.OK, RepositoryJson(archived: true));

        var answer = await probe.IsArchivedAsync(RepositoryOf(GitHubConnector()));

        Assert.True(answer);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("https://api.github.com/repos/acme/widgets", request.RequestUri!.ToString());
    }

    [Fact]
    public async Task A_repository_that_is_not_archived_any_more_is_reported_as_not_archived()
    {
        var (probe, handler) = CreateProbe();
        handler.Enqueue(HttpStatusCode.OK, RepositoryJson(archived: false));

        Assert.False(await probe.IsArchivedAsync(RepositoryOf(GitHubConnector())));
    }

    [Fact]
    public async Task A_not_modified_answer_repeats_the_last_answer()
    {
        var (probe, handler) = CreateProbe();
        var repository = RepositoryOf(GitHubConnector());
        handler.Enqueue(HttpStatusCode.OK, RepositoryJson(archived: true), "\"v1\"");
        handler.Enqueue(HttpStatusCode.NotModified, null, "\"v1\"");

        var first = await probe.IsArchivedAsync(repository);
        var second = await probe.IsArchivedAsync(repository);

        Assert.True(first);
        Assert.True(second);
        Assert.Equal("\"v1\"", handler.Requests[1].Headers.IfNoneMatch.Single().Tag);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task A_failed_request_is_no_answer(HttpStatusCode status)
    {
        var (probe, handler) = CreateProbe();
        handler.Enqueue(status, """{"message":"no"}""");

        Assert.Null(await probe.IsArchivedAsync(RepositoryOf(GitHubConnector())));
    }

    [Fact]
    public async Task An_answer_without_the_archived_field_is_no_answer()
    {
        var (probe, handler) = CreateProbe();
        handler.Enqueue(HttpStatusCode.OK, """{"id":1,"name":"widgets"}""");

        // No answer keeps the mark: a repository must not look writable because a field was missing.
        Assert.Null(await probe.IsArchivedAsync(RepositoryOf(GitHubConnector())));
    }

    [Fact]
    public async Task The_provider_is_not_asked_without_a_usable_GitHub_connector_or_address()
    {
        var (probe, handler) = CreateProbe();
        var removed = GitHubConnector();
        removed.RemovedAtUtc = DateTime.UtcNow;
        var nuget = GitHubConnector();
        nuget.ConnectorType = ConnectorType.NuGet;

        Assert.Null(await probe.IsArchivedAsync(RepositoryOf(null)));
        Assert.Null(await probe.IsArchivedAsync(RepositoryOf(removed)));
        Assert.Null(await probe.IsArchivedAsync(RepositoryOf(nuget)));
        Assert.Null(await probe.IsArchivedAsync(RepositoryOf(GitHubConnector(), "not a repository address")));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task The_verifier_clears_the_mark_when_GitHub_answers_not_archived_and_keeps_it_when_GitHub_fails()
    {
        await using var ctx = await CreateAsync();
        var graph = await SeedGraphAsync(ctx);
        var factory = ctx.Resolve<IDbContextFactory<AppDbContext>>();
        await using (var db = await factory.CreateDbContextAsync())
        {
            await db.Repositories
                .Where(r => r.RepositoryId == graph.ProducerId || r.RepositoryId == graph.ConsumerId)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.Archived, true));
            // The seeded connector has no token; the real service refuses to ask without one.
            await db.Connectors.ExecuteUpdateAsync(s => s
                .SetProperty(c => c.ConnectorType, ConnectorType.GitHub)
                .SetProperty(c => c.ApiBaseUrl, "https://api.github.com/")
                .SetProperty(c => c.UserToken, "test-token"));
        }
        var (probe, handler) = CreateProbe();
        // The verifier asks in order of repository name.
        var names = new[] { ConsumerName, ProducerName }.Order(StringComparer.Ordinal).ToList();
        handler.Enqueue(HttpStatusCode.OK, RepositoryJson(archived: false));
        handler.Enqueue(HttpStatusCode.InternalServerError, """{"message":"no"}""");
        var verifier = new ArchivedRepositoryVerifier(
            factory, probe, ctx.Resolve<IHubContext<WorkspaceSyncHub>>(), NullLogger<ArchivedRepositoryVerifier>.Instance);

        var cleared = await verifier.VerifyAsync(ctx.WorkspaceId);

        Assert.Equal([names[0]], cleared);
        Assert.Equal(2, handler.Requests.Count);
        await using var check = await factory.CreateDbContextAsync();
        var marks = await check.Repositories.AsNoTracking()
            .Where(r => r.RepositoryId == graph.ProducerId || r.RepositoryId == graph.ConsumerId)
            .ToDictionaryAsync(r => r.RepositoryName, r => r.Archived);
        Assert.False(marks[names[0]]);
        Assert.True(marks[names[1]]);
    }
}
