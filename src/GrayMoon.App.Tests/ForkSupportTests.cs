using System.Net;
using System.Text;
using GrayMoon.App.Components.Shared;
using GrayMoon.App.Data;
using GrayMoon.App.Models;
using GrayMoon.App.Repositories;
using GrayMoon.App.Services;
using GrayMoon.App.Services.GitHub;
using GrayMoon.App.Services.GitProviders;
using GrayMoon.App.Services.Queries;
using GrayMoon.App.Services.Workspaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GrayMoon.App.Tests;

/// <summary>Fork support: composing the fork/upstream pull requests, persisting and querying them, and detecting forks from GitHub.</summary>
public sealed class ForkSupportTests
{
    private const string ForkUrl = "https://github.com/acme/graymoon-api.git";
    private const string ParentUrl = "https://github.com/upstream/graymoon-api.git";

    private static PullRequestInfo Pr(int number, string state = "open", DateTimeOffset? mergedAt = null) =>
        new() { Number = number, State = state, MergedAt = mergedAt, HtmlUrl = $"https://github.com/x/y/pull/{number}" };

    // ---- PullRequestInfo.Compose -------------------------------------------------------------

    [Fact]
    public void Compose_with_neither_pull_request_is_null() =>
        Assert.Null(PullRequestInfo.Compose(null, null));

    [Fact]
    public void Compose_with_only_the_fork_pull_request_is_that_pull_request_unflagged()
    {
        var composed = PullRequestInfo.Compose(Pr(1), null);

        Assert.NotNull(composed);
        Assert.Equal(1, composed!.Number);
        Assert.False(composed.IsInParentRepository);
        Assert.Null(composed.ParentPullRequest);
    }

    [Fact]
    public void Compose_with_only_the_upstream_pull_request_flags_it_link_only()
    {
        var composed = PullRequestInfo.Compose(null, Pr(2));

        Assert.NotNull(composed);
        Assert.Equal(2, composed!.Number);
        Assert.True(composed.IsInParentRepository);
        Assert.Null(composed.ParentPullRequest);
    }

    [Fact]
    public void Compose_with_both_keeps_the_fork_pull_request_primary_and_attaches_the_upstream_one()
    {
        var own = Pr(1);
        var parent = Pr(2, "closed", DateTimeOffset.UtcNow);

        var composed = PullRequestInfo.Compose(own, parent);

        Assert.Equal(1, composed!.Number);
        Assert.False(composed.IsInParentRepository);
        Assert.NotNull(composed.ParentPullRequest);
        Assert.Equal(2, composed.ParentPullRequest!.Number);
        Assert.True(composed.ParentPullRequest.IsInParentRepository);
        Assert.True(composed.ParentPullRequest.IsMerged);
        Assert.Null(composed.ParentPullRequest.ParentPullRequest);
        // The inputs are never mutated: a cached value composed twice must not accumulate flags.
        Assert.False(own.IsInParentRepository);
        Assert.Null(own.ParentPullRequest);
        Assert.False(parent.IsInParentRepository);
    }

    [Fact]
    public void An_upstream_pull_request_does_not_offer_the_create_badge()
    {
        var upstreamOnly = PullRequestInfo.Compose(null, Pr(2));

        Assert.False(PRBadge.ShowsCreateBadge(false, true, upstreamOnly, defaultBranchAheadCommits: 3));
    }

    [Fact]
    public void ForkBadge_describes_the_parent_as_owner_and_repository()
    {
        Assert.Equal("upstream/graymoon-api", ForkBadge.DescribeParent(ParentUrl));
        Assert.Null(ForkBadge.DescribeParent(null));
        Assert.Null(ForkBadge.DescribeParent("not a url"));
    }

    // ---- Refresh and persistence -------------------------------------------------------------

    [Fact]
    public async Task Refresh_stores_both_pull_requests_for_a_fork_and_composes_them_on_read()
    {
        var provider = new FakeGitProvider { Own = Pr(10), Parent = Pr(20) };
        await using var ctx = await CreateForkContextAsync(provider);

        await RefreshAsync(ctx);

        var row = await ctx.ReadPullRequestAsync();
        Assert.Equal(10, row!.PullRequestNumber);
        Assert.Equal(20, row.ParentPullRequestNumber);

        var persisted = await ReadPersistedAsync(ctx);
        Assert.NotNull(persisted);
        Assert.Equal(10, persisted!.Number);
        Assert.Equal(20, persisted.ParentPullRequest!.Number);
        Assert.Contains((ForkUrl, ForkUrl), provider.Lookups);
        Assert.Contains((ParentUrl, ForkUrl), provider.Lookups);
    }

    [Fact]
    public async Task Refresh_with_only_an_upstream_pull_request_shows_it_as_the_single_link_only_pull_request()
    {
        await using var ctx = await CreateForkContextAsync(new FakeGitProvider { Own = null, Parent = Pr(20) });

        await RefreshAsync(ctx);

        var persisted = await ReadPersistedAsync(ctx);
        Assert.NotNull(persisted);
        Assert.Equal(20, persisted!.Number);
        Assert.True(persisted.IsInParentRepository);
        Assert.Null(persisted.ParentPullRequest);
    }

    [Fact]
    public async Task Refresh_with_only_a_fork_pull_request_behaves_as_before()
    {
        await using var ctx = await CreateForkContextAsync(new FakeGitProvider { Own = Pr(10), Parent = null });

        await RefreshAsync(ctx);

        var persisted = await ReadPersistedAsync(ctx);
        Assert.Equal(10, persisted!.Number);
        Assert.False(persisted.IsInParentRepository);
        Assert.Null(persisted.ParentPullRequest);
    }

    [Fact]
    public async Task Refresh_with_no_pull_request_anywhere_stores_a_cleared_row()
    {
        await using var ctx = await CreateForkContextAsync(new FakeGitProvider());

        await RefreshAsync(ctx);

        var byRepo = await GetByRepoAsync(ctx);
        Assert.True(byRepo.TryGetValue(ctx.RepositoryId, out var pr));
        Assert.Null(pr);
    }

    [Fact]
    public async Task A_repository_that_is_not_a_fork_is_never_looked_up_upstream()
    {
        var provider = new FakeGitProvider { Own = Pr(10), Parent = Pr(20) };
        await using var ctx = await CreateForkContextAsync(provider, isFork: false);

        await RefreshAsync(ctx);

        var persisted = await ReadPersistedAsync(ctx);
        Assert.Null(persisted!.ParentPullRequest);
        Assert.DoesNotContain(provider.Lookups, l => l.Target == ParentUrl);
    }

    [Fact]
    public async Task A_fork_whose_parent_is_unknown_is_only_looked_up_in_itself()
    {
        var provider = new FakeGitProvider { Own = Pr(10), Parent = Pr(20) };
        await using var ctx = await CreateForkContextAsync(provider, parentCloneUrl: null);

        await RefreshAsync(ctx);

        Assert.DoesNotContain(provider.Lookups, l => l.Target == ParentUrl);
        Assert.Null((await ReadPersistedAsync(ctx))!.ParentPullRequest);
    }

    [Fact]
    public async Task Clearing_the_pull_request_also_clears_the_upstream_one()
    {
        await using var ctx = await CreateForkContextAsync(new FakeGitProvider { Own = Pr(10), Parent = Pr(20) });
        await RefreshAsync(ctx);

        await using var scope = ctx.CreateScope();
        await scope.ServiceProvider.GetRequiredService<WorkspacePullRequestService>()
            .ClearPullRequestAsync(ctx.WorkspaceId, ctx.RepositoryId);

        var row = await ctx.ReadPullRequestAsync();
        Assert.Null(row!.PullRequestNumber);
        Assert.Null(row.ParentPullRequestNumber);
    }

    [Fact]
    public async Task Recording_a_merge_keeps_the_upstream_pull_request()
    {
        await using var ctx = await CreateForkContextAsync(new FakeGitProvider { Own = Pr(10), Parent = Pr(20) });
        await RefreshAsync(ctx);

        await using var scope = ctx.CreateScope();
        await scope.ServiceProvider.GetRequiredService<WorkspacePullRequestRepository>()
            .UpsertAsync(ctx.WorkspaceRepositoryId, new PullRequestInfo { Number = 10, State = "closed", MergedAt = DateTimeOffset.UtcNow });

        var row = await ctx.ReadPullRequestAsync();
        Assert.Equal(20, row!.ParentPullRequestNumber);
        Assert.Equal("closed", row.State);
    }

    // ---- Query layer -------------------------------------------------------------------------

    [Fact]
    public async Task List_query_exposes_fork_fields_and_the_upstream_pull_request()
    {
        await using var ctx = await CreateForkContextAsync(new FakeGitProvider { Own = null, Parent = Pr(20) });
        await RefreshAsync(ctx);

        await using var scope = ctx.CreateScope();
        var query = scope.ServiceProvider.GetRequiredService<IWorkspaceRepositoryLinkListQueryService>();
        var dto = Assert.Single(await query.GetAllSnapshotsAsync(ctx.WorkspaceId));

        Assert.True(dto.IsFork);
        Assert.Equal(ParentUrl, dto.ParentCloneUrl);
        Assert.Equal(20, dto.ParentPullRequestNumber);

        var link = WorkspaceRepositoryLinkListMapper.ToLink(dto);
        Assert.True(link.Repository!.IsFork);
        Assert.Equal(ParentUrl, link.Repository.ParentCloneUrl);
        var pr = WorkspaceRepositoryLinkListMapper.ToPullRequestInfo(dto);
        Assert.True(pr!.IsInParentRepository);
    }

    [Fact]
    public async Task An_upstream_pull_request_means_the_repository_is_not_creatable()
    {
        await using var ctx = await CreateForkContextAsync(new FakeGitProvider { Own = null, Parent = Pr(20) });
        await RefreshAsync(ctx);

        await using var scope = ctx.CreateScope();
        var query = scope.ServiceProvider.GetRequiredService<IWorkspaceRepositoryLinkListQueryService>();
        var header = await query.GetHeaderStateAsync(ctx.WorkspaceId);

        Assert.False(header.HasCreatablePr);
        Assert.False(header.HasOpenPr);
    }

    [Fact]
    public async Task Without_any_pull_request_the_fork_is_creatable()
    {
        await using var ctx = await CreateForkContextAsync(new FakeGitProvider());
        await RefreshAsync(ctx);

        await using var scope = ctx.CreateScope();
        var query = scope.ServiceProvider.GetRequiredService<IWorkspaceRepositoryLinkListQueryService>();

        Assert.True((await query.GetHeaderStateAsync(ctx.WorkspaceId)).HasCreatablePr);
    }

    // ---- Repository merge keeps fork information ---------------------------------------------

    [Fact]
    public async Task Merge_keeps_a_known_parent_when_the_fetch_could_not_resolve_it_and_clears_it_for_a_non_fork()
    {
        await using var ctx = await CreateForkContextAsync(new FakeGitProvider());
        await using (var scope = ctx.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var repo = await db.Repositories.SingleAsync();
            repo.GitHubRepositoryId = 100;
            await db.SaveChangesAsync();
        }

        async Task MergeAsync(bool isFork, string? parent)
        {
            await using var scope = ctx.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var existing = await db.Repositories.AsNoTracking().SingleAsync();
            await scope.ServiceProvider.GetRequiredService<GitHubRepositoryRepository>().MergeRepositoriesAsync(
            [
                new Repository
                {
                    ConnectorId = existing.ConnectorId,
                    RepositoryName = existing.RepositoryName,
                    OrgName = existing.OrgName,
                    Visibility = existing.Visibility,
                    CloneUrl = existing.CloneUrl,
                    GitHubRepositoryId = 100,
                    IsFork = isFork,
                    ParentCloneUrl = parent,
                }
            ]);
        }

        async Task<Repository> ReadAsync()
        {
            await using var scope = ctx.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Repositories.AsNoTracking().SingleAsync();
        }

        await MergeAsync(isFork: true, parent: ParentUrl);
        var stored = await ReadAsync();
        Assert.True(stored.IsFork);
        Assert.Equal(ParentUrl, stored.ParentCloneUrl);

        await MergeAsync(isFork: true, parent: null);
        Assert.Equal(ParentUrl, (await ReadAsync()).ParentCloneUrl);

        await MergeAsync(isFork: false, parent: null);
        var cleared = await ReadAsync();
        Assert.False(cleared.IsFork);
        Assert.Null(cleared.ParentCloneUrl);
    }

    [Fact]
    public async Task Migration_adds_the_fork_columns_and_is_idempotent()
    {
        await using var ctx = await CreateForkContextAsync(new FakeGitProvider());
        await using var scope = ctx.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var step = Migrations.StrictSteps.Single(s => s.Version == 15);
        await step.Action(db);
        await step.Action(db);

        Assert.Equal(1, await CountColumnAsync(db, "Repositories", "IsFork"));
        Assert.Equal(1, await CountColumnAsync(db, "WorkspaceRepositoryContextPullRequests", "ParentPullRequestNumber"));
    }

    private static async Task<int> CountColumnAsync(AppDbContext db, string table, string column)
    {
        var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
            await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = '{column}'";
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    // ---- GitHub fork detection ---------------------------------------------------------------

    [Fact]
    public async Task GitHub_reports_the_parent_of_a_fork_and_nothing_for_a_plain_repository()
    {
        var handler = new RoutingHandler();
        handler.Json("/repos/acme/fork", """{"id":1,"name":"fork","fork":true,"parent":{"full_name":"upstream/fork","clone_url":"https://github.com/upstream/fork.git"}}""");
        handler.Json("/repos/acme/plain", """{"id":2,"name":"plain","fork":false}""");
        var gitHub = CreateGitHub(handler);

        Assert.Equal("https://github.com/upstream/fork.git", await gitHub.GetForkParentCloneUrlAsync(Connector(), "acme", "fork"));
        Assert.Null(await gitHub.GetForkParentCloneUrlAsync(Connector(), "acme", "plain"));
    }

    [Fact]
    public async Task GitHub_looks_up_the_fork_branch_as_a_pull_request_in_the_upstream_repository()
    {
        var handler = new RoutingHandler();
        handler.Json("/repos/upstream/fork/pulls", """[{"number":5,"state":"open","html_url":"https://github.com/upstream/fork/pull/5","mergeable":true,"mergeable_state":"clean","head":{"ref":"feature/x","sha":"abc"},"base":{"ref":"main"}}]""");
        var pullRequests = new GitHubPullRequestService(CreateGitHub(handler), Options.Create(new WorkspaceOptions()), NullLogger<GitHubPullRequestService>.Instance);

        var pr = await pullRequests.GetPullRequestForBranchAsync(
            Connector(), "https://github.com/upstream/fork.git", "https://github.com/acme/fork.git", "feature/x");

        Assert.Equal(5, pr!.Number);
        var request = Assert.Single(handler.Requests, r => r.Contains("/repos/upstream/fork/pulls?", StringComparison.Ordinal));
        Assert.Contains("head=acme:feature%2Fx", request, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Fetching_repositories_resolves_a_fork_parent_once_and_reuses_the_stored_one()
    {
        var handler = new RoutingHandler();
        handler.Json("/user/repos", """
            [{"id":100,"node_id":"n1","name":"graymoon-api","full_name":"acme/graymoon-api","private":false,"archived":false,"clone_url":"https://github.com/acme/graymoon-api.git","owner":{"login":"acme"},"fork":true},
             {"id":101,"node_id":"n2","name":"plain","full_name":"acme/plain","private":false,"archived":false,"clone_url":"https://github.com/acme/plain.git","owner":{"login":"acme"},"fork":false}]
            """);
        handler.Json("/repos/acme/graymoon-api", """{"id":100,"name":"graymoon-api","fork":true,"parent":{"full_name":"upstream/graymoon-api","clone_url":"https://github.com/upstream/graymoon-api.git"}}""");
        await using var ctx = await SyncStateTestContext.CreateAsync(userToken: "token");
        await using var scope = ctx.CreateScope();
        var service = new GitHubRepositoryService(
            scope.ServiceProvider.GetRequiredService<ConnectorRepository>(),
            scope.ServiceProvider.GetRequiredService<GitHubRepositoryRepository>(),
            CreateGitHub(handler),
            Options.Create(new WorkspaceOptions { MaxParallelOperations = 2 }),
            NullLogger<GitHubRepositoryService>.Instance);

        await service.RefreshRepositoriesAsync();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var fork = await db.Repositories.AsNoTracking().SingleAsync(r => r.RepositoryName == "graymoon-api");
        var plain = await db.Repositories.AsNoTracking().SingleAsync(r => r.RepositoryName == "plain");
        Assert.True(fork.IsFork);
        Assert.Equal("https://github.com/upstream/graymoon-api.git", fork.ParentCloneUrl);
        Assert.False(plain.IsFork);
        Assert.Null(plain.ParentCloneUrl);
        Assert.Single(handler.Requests, r => r.StartsWith("/repos/acme/graymoon-api", StringComparison.Ordinal));
        Assert.DoesNotContain(handler.Requests, r => r.StartsWith("/repos/acme/plain", StringComparison.Ordinal));

        await service.RefreshRepositoriesAsync();
        Assert.Single(handler.Requests, r => r.StartsWith("/repos/acme/graymoon-api", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_failed_parent_lookup_keeps_the_fork_flag_and_does_not_fail_the_fetch()
    {
        var handler = new RoutingHandler();
        handler.Json("/user/repos", """
            [{"id":100,"node_id":"n1","name":"graymoon-api","full_name":"acme/graymoon-api","private":false,"archived":false,"clone_url":"https://github.com/acme/graymoon-api.git","owner":{"login":"acme"},"fork":true}]
            """);
        handler.Status("/repos/acme/graymoon-api", HttpStatusCode.InternalServerError);
        await using var ctx = await SyncStateTestContext.CreateAsync(userToken: "token");
        await using var scope = ctx.CreateScope();
        var service = new GitHubRepositoryService(
            scope.ServiceProvider.GetRequiredService<ConnectorRepository>(),
            scope.ServiceProvider.GetRequiredService<GitHubRepositoryRepository>(),
            CreateGitHub(handler),
            Options.Create(new WorkspaceOptions { MaxParallelOperations = 2 }),
            NullLogger<GitHubRepositoryService>.Instance);

        var result = await service.RefreshRepositoriesAsync();

        Assert.Empty(result.ConnectorErrors);
        var fork = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Repositories.AsNoTracking().SingleAsync(r => r.RepositoryName == "graymoon-api");
        Assert.True(fork.IsFork);
        Assert.Null(fork.ParentCloneUrl);
    }

    [Fact]
    public void The_provider_resolver_maps_GitHub_and_falls_back_to_a_provider_that_finds_nothing()
    {
        var handler = new RoutingHandler();
        var gitHubProvider = new GitHubGitProvider(
            new GitHubPullRequestService(CreateGitHub(handler), Options.Create(new WorkspaceOptions()), NullLogger<GitHubPullRequestService>.Instance));
        var resolver = new GitProviderResolver(gitHubProvider);

        Assert.Same(gitHubProvider, resolver.Get(ConnectorType.GitHub));
        Assert.Same(NoGitProvider.Instance, resolver.Get(ConnectorType.NuGet));
        Assert.Same(NoGitProvider.Instance, resolver.Get((ConnectorType)99));
    }

    // ---- Harness -----------------------------------------------------------------------------

    private static Connector Connector() => new()
    {
        ConnectorId = 1,
        ConnectorName = "github-test",
        ConnectorType = ConnectorType.GitHub,
        ApiBaseUrl = "https://api.github.com",
        UserToken = "token",
    };

    private static GitHubService CreateGitHub(HttpMessageHandler handler) => new(
        new HttpClient(handler),
        new ConfigurationBuilder().Build(),
        new GitHubRateLimitTracker(),
        new GitHubETagCache(),
        new FakeGitHubApiUsageRecorder(),
        NullLogger<GitHubService>.Instance);

    private static async Task<SyncStateTestContext> CreateForkContextAsync(FakeGitProvider provider, bool isFork = true, string? parentCloneUrl = ParentUrl)
    {
        var ctx = await SyncStateTestContext.CreateAsync(
            userToken: "token",
            configureServices: services => services.AddScoped<IGitProviderResolver>(_ => new FakeGitProviderResolver(provider)));
        var factory = ctx.Resolve<IDbContextFactory<AppDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        var repo = await db.Repositories.SingleAsync();
        repo.IsFork = isFork;
        repo.ParentCloneUrl = isFork ? parentCloneUrl : null;
        await db.SaveChangesAsync();
        return ctx;
    }

    private static async Task RefreshAsync(SyncStateTestContext ctx)
    {
        await using var scope = ctx.CreateScope();
        await scope.ServiceProvider.GetRequiredService<WorkspacePullRequestService>()
            .RefreshPullRequestsAsync(ctx.WorkspaceId, [ctx.RepositoryId], force: true);
    }

    private static async Task<IReadOnlyDictionary<int, PullRequestInfo?>> GetByRepoAsync(SyncStateTestContext ctx)
    {
        await using var scope = ctx.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<WorkspacePullRequestService>()
            .GetPersistedPullRequestsForWorkspaceAsync(ctx.WorkspaceId);
    }

    private static async Task<PullRequestInfo?> ReadPersistedAsync(SyncStateTestContext ctx) =>
        (await GetByRepoAsync(ctx)).GetValueOrDefault(ctx.RepositoryId);

    private sealed class FakeGitProviderResolver(IGitProvider provider) : IGitProviderResolver
    {
        public IGitProvider Get(ConnectorType connectorType) => provider;
    }

    /// <summary>Answers by the repository the lookup targets: <see cref="Own"/> for the fork, <see cref="Parent"/> for its upstream.</summary>
    private sealed class FakeGitProvider : IGitProvider
    {
        private readonly object _lock = new();
        private readonly List<(string Target, string Head)> _lookups = [];

        public PullRequestInfo? Own { get; init; }
        public PullRequestInfo? Parent { get; init; }
        public ConnectorType ConnectorType => ConnectorType.GitHub;

        public IReadOnlyList<(string Target, string Head)> Lookups
        {
            get
            {
                lock (_lock)
                    return _lookups.ToList();
            }
        }

        public Task<PullRequestInfo?> FindPullRequestForBranchAsync(
            Connector connector, string targetCloneUrl, string headCloneUrl, string branch, CancellationToken cancellationToken = default)
        {
            lock (_lock)
                _lookups.Add((targetCloneUrl, headCloneUrl));
            return Task.FromResult(targetCloneUrl == ForkUrl ? Clone(Own) : Clone(Parent));
        }

        public Task<PullRequestInfo?> GetPullRequestByNumberAsync(
            Connector connector, string targetCloneUrl, int number, string? headBranch, CancellationToken cancellationToken = default) =>
            Task.FromResult<PullRequestInfo?>(null);

        private static PullRequestInfo? Clone(PullRequestInfo? pr) => pr == null
            ? null
            : new PullRequestInfo { Number = pr.Number, State = pr.State, MergedAt = pr.MergedAt, HtmlUrl = pr.HtmlUrl };
    }

    private sealed class RoutingHandler : HttpMessageHandler
    {
        private readonly object _lock = new();
        private readonly Dictionary<string, (HttpStatusCode Status, string Body)> _routes = new(StringComparer.Ordinal);
        private readonly List<string> _requests = [];

        public IReadOnlyList<string> Requests
        {
            get
            {
                lock (_lock)
                    return _requests.ToList();
            }
        }

        public void Json(string path, string body) => _routes[path] = (HttpStatusCode.OK, body);

        public void Status(string path, HttpStatusCode status) => _routes[path] = (status, "{}");

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var pathAndQuery = request.RequestUri!.PathAndQuery;
            lock (_lock)
                _requests.Add(pathAndQuery);

            var path = request.RequestUri.AbsolutePath;
            if (_routes.TryGetValue(path, out var route))
            {
                return Task.FromResult(new HttpResponseMessage(route.Status)
                {
                    Content = new StringContent(route.Body, Encoding.UTF8, "application/json")
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") });
        }
    }
}
