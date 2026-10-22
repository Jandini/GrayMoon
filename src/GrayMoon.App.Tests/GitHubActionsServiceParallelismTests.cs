using System.Net;
using System.Text;
using GrayMoon.App.Data;
using GrayMoon.App.Models;
using GrayMoon.App.Repositories;
using GrayMoon.App.Services.GitHub;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GrayMoon.App.Tests;

/// <summary>
/// GitHubActionsService fans its per-repository / per-workflow GitHub calls out in parallel, bounded by
/// WorkspaceOptions.MaxParallelOperations, while keeping result order and per-item failure isolation.
/// </summary>
public sealed class GitHubActionsServiceParallelismTests
{
    private const int ItemCount = 8;

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public async Task GetLatestActions_processes_all_repositories_in_order_within_the_concurrency_limit(int limit)
    {
        await using var context = await ActionsTestContext.CreateAsync(limit, repositoryCount: ItemCount);

        var actions = await context.Service.GetLatestActionsAsync();

        var expectedOrder = (await context.RepositoryService.GetRepositoriesAsync()).Select(r => r.RepositoryName).ToList();
        Assert.Equal(ItemCount, actions.Count);
        Assert.Equal(expectedOrder, actions.Select(a => a.RepositoryName).ToList());
        AssertPeakBounded(context.Http.PeakConcurrency, limit);
    }

    [Fact]
    public async Task GetLatestActions_one_failing_repository_does_not_affect_the_others()
    {
        await using var context = await ActionsTestContext.CreateAsync(limit: 3, repositoryCount: ItemCount);
        context.Http.FailRepository = "repo3";

        var actions = await context.Service.GetLatestActionsAsync();

        Assert.Equal(ItemCount - 1, actions.Count);
        Assert.DoesNotContain(actions, a => a.RepositoryName == "repo3");
        var expectedOrder = (await context.RepositoryService.GetRepositoriesAsync())
            .Select(r => r.RepositoryName)
            .Where(name => name != "repo3")
            .ToList();
        Assert.Equal(expectedOrder, actions.Select(a => a.RepositoryName).ToList());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public async Task GetWorkflowStatusesForBranch_loads_workflow_metadata_in_order_within_the_concurrency_limit(int limit)
    {
        await using var context = await ActionsTestContext.CreateAsync(limit, repositoryCount: 1);
        context.Http.WorkflowCount = ItemCount;

        var statuses = await context.Service.GetWorkflowStatusesForBranchAsync(context.Repository, "main");

        Assert.NotNull(statuses);
        Assert.Equal(ItemCount, statuses!.Count);
        // Runs are ordered by workflow name, which is zero-padded so the expected order is the id order.
        Assert.Equal(Enumerable.Range(1, ItemCount).Select(i => (long?)i).ToList(), statuses.Select(s => s.WorkflowId).ToList());
        Assert.All(statuses, s => Assert.True(s.SupportsWorkflowDispatch));
        AssertPeakBounded(context.Http.PeakConcurrency, limit);
    }

    [Fact]
    public async Task GetWorkflowStatusesForBranch_rereads_a_transient_empty_runs_list_when_workflows_exist()
    {
        await using var context = await ActionsTestContext.CreateAsync(limit: 1, repositoryCount: 1);
        context.Http.HasActiveWorkflow = true;
        context.Http.WorkflowCount = 1;
        context.Http.EmptyRunsResponses = 1;

        var statuses = await context.Service.GetWorkflowStatusesForBranchAsync(context.Repository, "main");

        Assert.NotNull(statuses);
        Assert.Equal("success", Assert.Single(statuses!).Status);
        Assert.Equal(2, context.Http.RunsRequestCount);
    }

    [Fact]
    public async Task GetWorkflowStatusesForBranch_reports_none_when_the_runs_list_stays_empty()
    {
        await using var context = await ActionsTestContext.CreateAsync(limit: 1, repositoryCount: 1);
        context.Http.HasActiveWorkflow = true;
        context.Http.WorkflowCount = 1;
        context.Http.EmptyRunsResponses = int.MaxValue;

        var statuses = await context.Service.GetWorkflowStatusesForBranchAsync(context.Repository, "main");

        Assert.NotNull(statuses);
        Assert.Equal("none", Assert.Single(statuses!).Status);
        Assert.Equal(3, context.Http.RunsRequestCount);
    }

    private static void AssertPeakBounded(int peak, int limit)
    {
        Assert.True(peak <= limit, $"Peak concurrency {peak} exceeded limit {limit}.");
        Assert.True(peak >= limit, $"Peak concurrency {peak} never reached limit {limit}.");
    }

    private sealed class FakeActionsHandler : HttpMessageHandler
    {
        private int _current;
        private int _peak;

        public int PeakConcurrency => Volatile.Read(ref _peak);
        public string? FailRepository { get; set; }
        public int WorkflowCount { get; set; }
        public bool HasActiveWorkflow { get; set; }
        public int EmptyRunsResponses { get; set; }
        public int RunsRequestCount => Volatile.Read(ref _runsRequests);
        private int _runsRequests;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!.PathAndQuery;

            if (uri.EndsWith("/actions/workflows", StringComparison.Ordinal))
                return Json(HasActiveWorkflow
                    ? """{"workflows":[{"id":1,"name":"wf01","path":".github/workflows/wf01.yml","html_url":"","state":"active"}]}"""
                    : """{"workflows":[]}""");
            if (uri.Contains("/actions/runs?branch=", StringComparison.Ordinal))
            {
                if (Interlocked.Increment(ref _runsRequests) <= EmptyRunsResponses)
                    return Json("""{"total_count":0,"workflow_runs":[]}""");

                var runs = string.Join(",", Enumerable.Range(1, WorkflowCount).Select(i =>
                    $$"""{"id":{{i}},"workflow_id":{{i}},"name":"wf{{i:D2}}","event":"push","head_branch":"main","status":"completed","conclusion":"success","updated_at":"2026-10-06T00:00:00Z","html_url":""}"""));
                return Json($$"""{"workflow_runs":[{{runs}}]}""");
            }

            var inFlight = Interlocked.Increment(ref _current);
            try
            {
                int seen;
                while (inFlight > (seen = Volatile.Read(ref _peak)) && Interlocked.CompareExchange(ref _peak, inFlight, seen) != seen)
                {
                }

                // Varied latency so completion order differs from start order.
                var delay = 40 + (uri.GetHashCode(StringComparison.Ordinal) & 3) * 15;
                await Task.Delay(delay, cancellationToken);

                if (uri.Contains("/actions/runs?per_page=1", StringComparison.Ordinal))
                {
                    var repo = uri.Split('/')[3];
                    if (repo == FailRepository)
                        return new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("{}") };
                    return Json($$"""{"workflow_runs":[{"id":1,"workflow_id":1,"name":"{{repo}}","event":"push","head_branch":"main","status":"completed","conclusion":"success","updated_at":"2026-10-06T00:00:00Z","html_url":""}]}""");
                }

                if (uri.Contains("/contents/", StringComparison.Ordinal))
                {
                    var yaml = Convert.ToBase64String(Encoding.UTF8.GetBytes("on:\n  workflow_dispatch:\n"));
                    return Json($$"""{"type":"file","encoding":"base64","content":"{{yaml}}"}""");
                }

                if (uri.Contains("/actions/workflows/", StringComparison.Ordinal))
                {
                    var id = uri[(uri.LastIndexOf('/') + 1)..];
                    return Json($$"""{"id":{{id}},"name":"wf{{id}}","path":".github/workflows/wf{{id}}.yml","html_url":"","state":"active"}""");
                }

                return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") };
            }
            finally
            {
                Interlocked.Decrement(ref _current);
            }
        }

        private static HttpResponseMessage Json(string body) =>
            new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    private sealed class ActionsTestContext : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly AppDbContext _dbContext;
        private readonly MemoryCache _memoryCache = new(new MemoryCacheOptions());

        private ActionsTestContext(SqliteConnection connection, AppDbContext dbContext, int limit)
        {
            _connection = connection;
            _dbContext = dbContext;
            var gitHub = new GitHubService(
                new HttpClient(Http),
                new ConfigurationBuilder().Build(),
                new GitHubRateLimitTracker(),
                new GitHubETagCache(),
                new FakeGitHubApiUsageRecorder(),
                NullLogger<GitHubService>.Instance);
            var connectorRepository = new ConnectorRepository(dbContext, NullLogger<ConnectorRepository>.Instance);
            RepositoryService = new GitHubRepositoryService(
                connectorRepository,
                new GitHubRepositoryRepository(dbContext, new RecordingGitChangesNotifier(), NullLogger<GitHubRepositoryRepository>.Instance),
                gitHub,
                Options.Create(new WorkspaceOptions()),
                NullLogger<GitHubRepositoryService>.Instance);
            Service = new GitHubActionsService(
                connectorRepository,
                RepositoryService,
                gitHub,
                _memoryCache,
                Options.Create(new WorkspaceOptions { MaxParallelOperations = limit }),
                NullLogger<GitHubActionsService>.Instance);
        }

        public FakeActionsHandler Http { get; } = new();
        public GitHubActionsService Service { get; }
        public GitHubRepositoryService RepositoryService { get; }
        public GitHubRepositoryEntry Repository { get; private set; } = new();

        public static async Task<ActionsTestContext> CreateAsync(int limit, int repositoryCount)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var dbContext = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
            await dbContext.Database.EnsureCreatedAsync();

            var context = new ActionsTestContext(connection, dbContext, limit);
            await context.SeedAsync(repositoryCount);
            return context;
        }

        private async Task SeedAsync(int repositoryCount)
        {
            var connector = new Connector
            {
                ConnectorName = "GitHub",
                ConnectorType = ConnectorType.GitHub,
                ApiBaseUrl = "https://api.github.com/",
                UserToken = "test-token"
            };
            _dbContext.Connectors.Add(connector);
            await _dbContext.SaveChangesAsync();

            for (var i = 1; i <= repositoryCount; i++)
            {
                _dbContext.Repositories.Add(new Repository
                {
                    ConnectorId = connector.ConnectorId,
                    RepositoryName = $"repo{i}",
                    OrgName = "acme",
                    CloneUrl = $"https://github.com/acme/repo{i}.git"
                });
            }

            await _dbContext.SaveChangesAsync();
            Repository = (await RepositoryService.GetRepositoriesAsync()).First();
        }

        public async ValueTask DisposeAsync()
        {
            _memoryCache.Dispose();
            await _dbContext.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }
}
