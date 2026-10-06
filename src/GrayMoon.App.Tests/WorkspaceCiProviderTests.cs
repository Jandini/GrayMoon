using System.Net;
using System.Text;
using GrayMoon.Abstractions.Workspaces;
using GrayMoon.App.Data;
using GrayMoon.App.Models;
using GrayMoon.App.Repositories;
using GrayMoon.App.Services;
using GrayMoon.App.Services.Ci;
using GrayMoon.App.Services.Workspaces;
using GrayMoon.Application.Features;
using GrayMoon.Application.Workspaces;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GrayMoon.App.Tests;

/// <summary>
/// The CI provider boundary: CI=None does no GitHub Actions query, refresh, persistence or push-time run watching;
/// CI=GitHubActions behaves as the Actions services always did; GitHub source control is unaffected by either.
/// </summary>
public sealed class WorkspaceCiProviderTests
{
    private const string Branch = "main";
    private const long RunningRunId = 99;

    [Fact]
    public async Task Resolver_selects_the_no_op_provider_for_None_and_the_GitHub_Actions_provider_for_GitHubActions()
    {
        await using var context = await CiTestContext.CreateAsync(WorkspaceCiProvider.None);
        var gitHubActionsWorkspaceId = await context.AddWorkspaceAsync("gha", WorkspaceCiProvider.GitHubActions);

        var none = await context.Resolver.GetForWorkspaceAsync(context.WorkspaceId);
        var gitHubActions = await context.Resolver.GetForWorkspaceAsync(gitHubActionsWorkspaceId);

        Assert.Same(NoCiProvider.Instance, none);
        Assert.Equal(WorkspaceCiProvider.None, none.Kind);
        Assert.IsType<GitHubActionsCiProvider>(gitHubActions);
        Assert.Equal(WorkspaceCiProvider.GitHubActions, gitHubActions.Kind);
        Assert.Same(gitHubActions, context.Resolver.Get(WorkspaceCiProvider.GitHubActions));
        Assert.Same(NoCiProvider.Instance, context.Resolver.Get(WorkspaceCiProvider.None));
    }

    [Fact]
    public async Task Resolver_treats_an_unknown_ci_value_as_no_ci()
    {
        await using var context = await CiTestContext.CreateAsync(WorkspaceCiProvider.None);

        Assert.Same(NoCiProvider.Instance, context.Resolver.Get((WorkspaceCiProvider)42));
    }

    [Theory]
    [InlineData(WorkspaceCiProvider.None)]
    [InlineData(WorkspaceCiProvider.GitHubActions)]
    public async Task Provider_IsEnabled_matches_the_UsesCiIntegration_capability(WorkspaceCiProvider ciProvider)
    {
        await using var context = await CiTestContext.CreateAsync(ciProvider);

        var capabilities = new WorkspaceCapabilities(WorkspaceType.DotNetDependency, WorkspaceVersioningMode.GitVersion, ciProvider);
        var provider = await context.Resolver.GetForWorkspaceAsync(context.WorkspaceId);

        Assert.Equal(capabilities.UsesCiIntegration, provider.IsEnabled);
    }

    [Fact]
    public async Task None_refresh_makes_no_GitHub_call_and_persists_nothing()
    {
        await using var context = await CiTestContext.CreateAsync(WorkspaceCiProvider.None);
        var provider = await context.Resolver.GetForWorkspaceAsync(context.WorkspaceId);

        var legacy = await provider.RefreshStatusesAsync(null, context.WorkspaceRepositoryId, context.RepositoryEntry, Branch);
        var feature = await provider.RefreshStatusesAsync(
            await context.AddFeatureContextAsync(), context.WorkspaceRepositoryId, context.RepositoryEntry, Branch);

        Assert.Null(legacy);
        Assert.Null(feature);
        Assert.Empty(context.Http.Requests);
        Assert.Equal(0, await context.DbContext.WorkspaceRepositoryActions.CountAsync());
        Assert.Equal(0, await context.DbContext.WorkspaceRepositoryContextActions.CountAsync());
    }

    [Fact]
    public async Task None_reports_no_persisted_status_even_when_an_earlier_GitHub_Actions_row_exists()
    {
        await using var context = await CiTestContext.CreateAsync(WorkspaceCiProvider.None);
        context.DbContext.WorkspaceRepositoryActions.Add(new WorkspaceRepositoryAction
        {
            WorkspaceRepositoryId = context.WorkspaceRepositoryId,
            BranchName = Branch,
            WorkflowsJson = "[]",
            LastCheckedAt = DateTime.UtcNow
        });
        await context.DbContext.SaveChangesAsync();
        var provider = await context.Resolver.GetForWorkspaceAsync(context.WorkspaceId);

        var persisted = await provider.GetPersistedStatusesAsync(context.WorkspaceId, featureContextId: null);

        Assert.Empty(persisted);
    }

    [Fact]
    public async Task GitHubActions_refresh_fetches_live_status_and_persists_it_on_the_special_Workspace_link()
    {
        await using var context = await CiTestContext.CreateAsync(WorkspaceCiProvider.GitHubActions);
        var provider = await context.Resolver.GetForWorkspaceAsync(context.WorkspaceId);

        var statuses = await provider.RefreshStatusesAsync(null, context.WorkspaceRepositoryId, context.RepositoryEntry, Branch);

        var status = Assert.Single(statuses!);
        Assert.Equal("running", status.Status);
        Assert.Equal(RunningRunId, status.RunId);
        Assert.Contains(context.Http.Requests, uri => uri.Contains("/actions/workflows", StringComparison.Ordinal));
        Assert.Contains(context.Http.Requests, uri => uri.Contains("/actions/runs?branch=main", StringComparison.Ordinal));

        var row = Assert.Single(await context.DbContext.WorkspaceRepositoryActions.AsNoTracking().ToListAsync());
        Assert.Equal(context.WorkspaceRepositoryId, row.WorkspaceRepositoryId);
        Assert.Equal(0, await context.DbContext.WorkspaceRepositoryContextActions.CountAsync());

        var persisted = await provider.GetPersistedStatusesAsync(context.WorkspaceId, featureContextId: null);
        Assert.Equal(RunningRunId, Assert.Single(persisted[context.RepositoryId].Workflows).RunId);
    }

    [Fact]
    public async Task GitHubActions_refresh_for_a_Feature_context_persists_only_the_context_row()
    {
        await using var context = await CiTestContext.CreateAsync(WorkspaceCiProvider.GitHubActions);
        var featureContextId = await context.AddFeatureContextAsync();
        var provider = await context.Resolver.GetForWorkspaceAsync(context.WorkspaceId);

        await provider.RefreshStatusesAsync(featureContextId, context.WorkspaceRepositoryId, context.RepositoryEntry, Branch);

        var row = Assert.Single(await context.DbContext.WorkspaceRepositoryContextActions.AsNoTracking().ToListAsync());
        Assert.Equal(featureContextId.Value, row.WorkspaceFeatureContextId);
        Assert.Equal(0, await context.DbContext.WorkspaceRepositoryActions.CountAsync());

        Assert.Empty(await provider.GetPersistedStatusesAsync(context.WorkspaceId, featureContextId: null));
        Assert.Single(await provider.GetPersistedStatusesAsync(context.WorkspaceId, featureContextId));
    }

    [Fact]
    public async Task None_push_run_watch_never_queries_GitHub_Actions()
    {
        await using var context = await CiTestContext.CreateAsync(WorkspaceCiProvider.None);
        var provider = await context.Resolver.GetForWorkspaceAsync(context.WorkspaceId);
        var overlay = new OverlayCommandTerminalService();

        var watch = provider.CreatePushRunWatch(overlay);
        await watch.TickAsync(context.PushedRepos, await context.LoadLinksAsync(), CancellationToken.None);

        Assert.Same(NoOpPushCiRunWatch.Instance, watch);
        Assert.Empty(context.Http.Requests);
        Assert.Empty(overlay.GetSnapshot());
    }

    [Fact]
    public async Task GitHubActions_push_run_watch_discovers_the_running_workflow_and_streams_its_jobs()
    {
        await using var context = await CiTestContext.CreateAsync(WorkspaceCiProvider.GitHubActions);
        var provider = await context.Resolver.GetForWorkspaceAsync(context.WorkspaceId);
        var overlay = new OverlayCommandTerminalService();

        var watch = provider.CreatePushRunWatch(overlay);
        await watch.TickAsync(context.PushedRepos, await context.LoadLinksAsync(), CancellationToken.None);

        Assert.Contains(context.Http.Requests, uri => uri.Contains("/actions/runs?branch=main", StringComparison.Ordinal));
        Assert.Contains(context.Http.Requests, uri => uri.Contains($"/actions/runs/{RunningRunId}/jobs", StringComparison.Ordinal));
        var lines = overlay.GetSnapshot();
        Assert.Contains(lines, line => line.StreamLabel == "gha:widgets" && line.Text.Contains($"Run #{RunningRunId}", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.StreamLabel == "gha:widgets" && line.Text.StartsWith("Live - 1 job(s)", StringComparison.Ordinal));
        Assert.Equal(0, await context.DbContext.WorkspaceRepositoryActions.CountAsync());
    }

    [Fact]
    public async Task GitHubActions_push_run_watch_without_an_overlay_is_a_no_op()
    {
        await using var context = await CiTestContext.CreateAsync(WorkspaceCiProvider.GitHubActions);
        var provider = await context.Resolver.GetForWorkspaceAsync(context.WorkspaceId);

        var watch = provider.CreatePushRunWatch(overlayTerminal: null);
        await watch.TickAsync(context.PushedRepos, await context.LoadLinksAsync(), CancellationToken.None);

        Assert.Same(NoOpPushCiRunWatch.Instance, watch);
        Assert.Empty(context.Http.Requests);
    }

    [Fact]
    public async Task Pull_request_lookup_still_reaches_GitHub_when_the_workspace_has_no_CI()
    {
        await using var context = await CiTestContext.CreateAsync(WorkspaceCiProvider.None);
        var provider = await context.Resolver.GetForWorkspaceAsync(context.WorkspaceId);
        await provider.RefreshStatusesAsync(null, context.WorkspaceRepositoryId, context.RepositoryEntry, Branch);
        var pullRequests = new GitHubPullRequestService(
            context.GitHub,
            Options.Create(new WorkspaceOptions()),
            NullLogger<GitHubPullRequestService>.Instance);
        var repository = await context.DbContext.Repositories.AsNoTracking().Include(r => r.Connector).SingleAsync();

        var pullRequest = await pullRequests.GetPullRequestForBranchAsync(repository, repository.Connector, Branch);

        Assert.NotNull(pullRequest);
        Assert.Equal(7, pullRequest!.Number);
        Assert.Contains(context.Http.Requests, uri => uri.Contains("/repos/acme/widgets/pulls?", StringComparison.Ordinal));
        Assert.DoesNotContain(context.Http.Requests, uri => uri.Contains("/actions/", StringComparison.Ordinal));
    }

    /// <summary>Serves just enough of the GitHub API for one active workflow with one running run, plus one open PR.</summary>
    private sealed class FakeGitHubHandler : HttpMessageHandler
    {
        private readonly object _lock = new();
        private readonly List<string> _requests = [];

        public IReadOnlyList<string> Requests
        {
            get
            {
                lock (_lock)
                    return _requests.ToList();
            }
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!.PathAndQuery;
            lock (_lock)
                _requests.Add(uri);

            if (uri.EndsWith("/actions/workflows", StringComparison.Ordinal))
                return Json("""{"workflows":[{"id":1,"name":"build","path":".github/workflows/build.yml","html_url":"","state":"active"}]}""");
            if (uri.Contains("/actions/runs?branch=", StringComparison.Ordinal))
                return Json($$"""{"workflow_runs":[{"id":{{RunningRunId}},"workflow_id":1,"name":"build","event":"push","head_branch":"main","status":"in_progress","conclusion":null,"updated_at":"2026-10-06T00:00:00Z","html_url":""}]}""");
            if (uri.Contains($"/actions/runs/{RunningRunId}/jobs", StringComparison.Ordinal))
                return Json("""{"total_count":1,"jobs":[{"id":5,"name":"build","status":"in_progress","conclusion":null,"steps":[{"name":"compile","status":"in_progress","conclusion":null,"number":1}]}]}""");
            if (uri.Contains("/pulls?", StringComparison.Ordinal))
                return Json("""[{"number":7,"state":"open","title":"Work","html_url":"https://github.com/acme/widgets/pull/7","mergeable":true,"mergeable_state":"clean","head":{"ref":"main","sha":"abc"},"base":{"ref":"develop"}}]""");

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") });
        }

        private static Task<HttpResponseMessage> Json(string body) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
    }

    private sealed class CiTestContext : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly MemoryCache _memoryCache = new(new MemoryCacheOptions());

        private CiTestContext(SqliteConnection connection, AppDbContext dbContext)
        {
            _connection = connection;
            DbContext = dbContext;
            GitHub = new GitHubService(
                new HttpClient(Http),
                new ConfigurationBuilder().Build(),
                new GitHubRateLimitTracker(),
                new GitHubETagCache(),
                new FakeGitHubApiUsageRecorder(),
                NullLogger<GitHubService>.Instance);

            var connectorRepository = new ConnectorRepository(dbContext, NullLogger<ConnectorRepository>.Instance);
            var gitHubActions = new GitHubActionsService(
                connectorRepository,
                new GitHubRepositoryService(
                    connectorRepository,
                    new GitHubRepositoryRepository(dbContext, new RecordingGitChangesNotifier(), NullLogger<GitHubRepositoryRepository>.Instance),
                    GitHub,
                    NullLogger<GitHubRepositoryService>.Instance),
                GitHub,
                _memoryCache,
                NullLogger<GitHubActionsService>.Instance);
            var gitHubActionsProvider = new GitHubActionsCiProvider(
                new WorkspaceActionService(new WorkspaceActionRepository(dbContext, NullLogger<WorkspaceActionRepository>.Instance), gitHubActions),
                gitHubActions,
                new GhaWorkflowLiveFeedService(
                    GitHub,
                    connectorRepository,
                    new GitHubRateLimitTracker(),
                    new GhaLiveFeedJobsCache(),
                    NullLogger<GhaWorkflowLiveFeedService>.Instance),
                NullLogger<GitHubActionsCiProvider>.Instance);

            Resolver = new WorkspaceCiProviderResolver(
                new WorkspaceCapabilitiesResolver(new TestDbContextFactory(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options)),
                gitHubActionsProvider);
        }

        public FakeGitHubHandler Http { get; } = new();
        public AppDbContext DbContext { get; }
        public GitHubService GitHub { get; }
        public WorkspaceCiProviderResolver Resolver { get; }
        public int WorkspaceId { get; private set; }
        public int RepositoryId { get; private set; }
        public int WorkspaceRepositoryId { get; private set; }
        public GitHubRepositoryEntry RepositoryEntry { get; private set; } = new();
        public IReadOnlyList<PushRepoPayload> PushedRepos => [new PushRepoPayload(RepositoryId, "widgets", 1, [])];

        public static async Task<CiTestContext> CreateAsync(WorkspaceCiProvider ciProvider)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var dbContext = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
            await dbContext.Database.EnsureCreatedAsync();

            var context = new CiTestContext(connection, dbContext);
            await context.SeedAsync(ciProvider);
            return context;
        }

        public async Task<int> AddWorkspaceAsync(string name, WorkspaceCiProvider ciProvider)
        {
            var workspace = new Workspace
            {
                Name = name,
                Type = WorkspaceType.DotNetDependency,
                VersioningMode = WorkspaceVersioningMode.GitVersion,
                CiProvider = ciProvider
            };
            DbContext.Workspaces.Add(workspace);
            await DbContext.SaveChangesAsync();
            return workspace.WorkspaceId;
        }

        public async Task<WorkspaceFeatureContextId> AddFeatureContextAsync()
        {
            var feature = new WorkspaceFeature
            {
                WorkspaceId = WorkspaceId,
                Name = "feat",
                LifecycleState = WorkspaceFeatureLifecycleState.Ready,
                BaseKind = WorkspaceFeatureBaseKind.CurrentWorkspace,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            DbContext.WorkspaceFeatures.Add(feature);
            await DbContext.SaveChangesAsync();

            var featureContext = new WorkspaceFeatureContext
            {
                WorkspaceId = WorkspaceId,
                Kind = WorkspaceFeatureContextKind.Feature,
                WorkspaceFeatureId = feature.WorkspaceFeatureId,
                CreatedAt = DateTime.UtcNow,
                IsInSync = true
            };
            DbContext.WorkspaceFeatureContexts.Add(featureContext);
            await DbContext.SaveChangesAsync();
            return new WorkspaceFeatureContextId(featureContext.WorkspaceFeatureContextId);
        }

        public async Task<IReadOnlyList<WorkspaceRepositoryLink>> LoadLinksAsync() =>
            await DbContext.WorkspaceRepositories
                .AsNoTracking()
                .Include(wr => wr.Repository)
                .ThenInclude(r => r!.Connector)
                .Where(wr => wr.WorkspaceId == WorkspaceId)
                .ToListAsync();

        private async Task SeedAsync(WorkspaceCiProvider ciProvider)
        {
            var connector = new Connector
            {
                ConnectorName = "GitHub",
                ConnectorType = ConnectorType.GitHub,
                ApiBaseUrl = "https://api.github.com/",
                UserToken = "test-token"
            };
            DbContext.Connectors.Add(connector);
            await DbContext.SaveChangesAsync();

            var repository = new Repository
            {
                ConnectorId = connector.ConnectorId,
                RepositoryName = "widgets",
                OrgName = "acme",
                CloneUrl = "https://github.com/acme/widgets.git"
            };
            DbContext.Repositories.Add(repository);
            await DbContext.SaveChangesAsync();

            WorkspaceId = await AddWorkspaceAsync("workspace", ciProvider);

            var link = new WorkspaceRepositoryLink
            {
                WorkspaceId = WorkspaceId,
                RepositoryId = repository.RepositoryId,
                BranchName = Branch
            };
            DbContext.WorkspaceRepositories.Add(link);
            await DbContext.SaveChangesAsync();

            RepositoryId = repository.RepositoryId;
            WorkspaceRepositoryId = link.WorkspaceRepositoryId;
            RepositoryEntry = new GitHubRepositoryEntry
            {
                RepositoryId = repository.RepositoryId,
                ConnectorName = connector.ConnectorName,
                OrgName = repository.OrgName,
                RepositoryName = repository.RepositoryName,
                CloneUrl = repository.CloneUrl
            };
        }

        public async ValueTask DisposeAsync()
        {
            _memoryCache.Dispose();
            await DbContext.DisposeAsync();
            await _connection.DisposeAsync();
        }

        private sealed class TestDbContextFactory(DbContextOptions<AppDbContext> options) : IDbContextFactory<AppDbContext>
        {
            public AppDbContext CreateDbContext() => new(options);
        }
    }
}
