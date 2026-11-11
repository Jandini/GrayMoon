using GrayMoon.Abstractions.Exceptions;
using GrayMoon.App.Data;
using GrayMoon.App.Models;
using GrayMoon.App.Repositories;
using GrayMoon.App.Services.Connectors;
using GrayMoon.App.Services.GitChanges;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GrayMoon.App.Tests;

/// <summary>
/// B5: a connector refresh (<see cref="GitHubRepositoryRepository.MergeRepositoriesAsync"/> PHASE E) or a
/// connector delete (<see cref="ConnectorRepository.DeleteAsync"/>) must never delete a
/// WorkspaceRepositoryLink row that a Feature uses. WorkspaceFeatureRepositories cascades from that link
/// (FK_WorkspaceFeatureRepositories_Links, configured in AppDbContext.Features.cs), so deleting the link
/// would silently delete the Feature's own repository row and leave its worktree without a database record.
/// </summary>
public sealed class ConnectorRepositoryFeatureProtectionTests
{
    [Fact]
    public async Task MergeRepositoriesAsync_keeps_and_marks_a_workspace_linked_repository_the_provider_no_longer_returns()
    {
        // Connector state never removes a Workspace's repository. With no Feature at all, the linked repository
        // that is no longer returned used to be deleted with its link; it is kept, marked and reported.
        await using var ctx = await MergeTestContext.CreateAsync();
        await using var scope = ctx.CreateScope();
        var repo = ctx.MergeRepo(scope);
        var db = ctx.Db(scope);

        var fetched = new List<Repository> { ctx.FetchedKeptRepository() };

        var result = await repo.MergeRepositoriesAsync(fetched);

        var kept = Assert.Single(result.KeptMissingFromProvider);
        Assert.Equal(ctx.DroppedRepositoryId, kept.RepositoryId);
        Assert.Equal(new[] { "merge-test-ws" }, kept.WorkspaceNames);
        Assert.Empty(kept.FeatureNames);
        Assert.True(await db.WorkspaceRepositories.AnyAsync(l => l.RepositoryId == ctx.DroppedRepositoryId));
        Assert.NotNull((await db.Repositories.AsNoTracking().SingleAsync(r => r.RepositoryId == ctx.DroppedRepositoryId)).MissingFromProviderSinceUtc);
        Assert.Null((await db.Repositories.AsNoTracking().SingleAsync(r => r.RepositoryId == ctx.KeptRepositoryId)).MissingFromProviderSinceUtc);
    }

    [Fact]
    public async Task MergeRepositoriesAsync_clears_the_mark_when_the_provider_returns_the_repository_again()
    {
        await using var ctx = await MergeTestContext.CreateAsync();
        await using (var scope = ctx.CreateScope())
            await ctx.MergeRepo(scope).MergeRepositoriesAsync([ctx.FetchedKeptRepository()]);

        await using var secondScope = ctx.CreateScope();
        var db = ctx.Db(secondScope);
        var dropped = await db.Repositories.AsNoTracking().SingleAsync(r => r.RepositoryId == ctx.DroppedRepositoryId);
        Assert.NotNull(dropped.MissingFromProviderSinceUtc);

        var result = await ctx.MergeRepo(secondScope).MergeRepositoriesAsync(
        [
            ctx.FetchedKeptRepository(),
            new Repository { ConnectorId = ctx.ConnectorId, RepositoryName = dropped.RepositoryName, OrgName = dropped.OrgName, Visibility = "Public", CloneUrl = dropped.CloneUrl }
        ]);

        Assert.Empty(result.KeptMissingFromProvider);
        Assert.Null((await db.Repositories.AsNoTracking().SingleAsync(r => r.RepositoryId == ctx.DroppedRepositoryId)).MissingFromProviderSinceUtc);
    }

    [Fact]
    public async Task MergeRepositoriesAsync_deletes_an_unlinked_repository_the_provider_no_longer_returns()
    {
        // The catalog (repositories no Workspace links) still follows the provider.
        await using var ctx = await MergeTestContext.CreateAsync();
        var unlinkedId = await ctx.AddRepositoryAsync(ctx.ConnectorId, "repo-unlinked", linked: false);
        await using var scope = ctx.CreateScope();
        var db = ctx.Db(scope);

        await ctx.MergeRepo(scope).MergeRepositoriesAsync([ctx.FetchedKeptRepository()]);

        Assert.False(await db.Repositories.AnyAsync(r => r.RepositoryId == unlinkedId));
    }

    [Fact]
    public async Task MergeRepositoriesAsync_touches_nothing_of_a_connector_that_returned_no_repository()
    {
        // A connector with nothing in the fetched set (failed, inactive, or an empty answer) did not say its
        // repositories are gone. Before the fix both of these rows were deleted.
        await using var ctx = await MergeTestContext.CreateAsync();
        var otherConnectorId = await ctx.AddConnectorAsync("github-other");
        var linkedId = await ctx.AddRepositoryAsync(otherConnectorId, "other-linked", linked: true);
        var unlinkedId = await ctx.AddRepositoryAsync(otherConnectorId, "other-unlinked", linked: false);
        await using var scope = ctx.CreateScope();
        var db = ctx.Db(scope);

        var result = await ctx.MergeRepo(scope).MergeRepositoriesAsync([ctx.FetchedKeptRepository()]);

        Assert.DoesNotContain(result.KeptMissingFromProvider, k => k.RepositoryId == linkedId);
        Assert.True(await db.WorkspaceRepositories.AnyAsync(l => l.RepositoryId == linkedId));
        Assert.Null((await db.Repositories.AsNoTracking().SingleAsync(r => r.RepositoryId == linkedId)).MissingFromProviderSinceUtc);
        Assert.True(await db.Repositories.AnyAsync(r => r.RepositoryId == unlinkedId));
    }

    [Fact]
    public async Task MergeRepositoriesAsync_keeps_repository_still_used_by_a_feature()
    {
        // Step 1: the connector no longer returns the dropped repository, but a Feature still uses it.
        // The repository, its link, and its Feature row all survive, and the result names the Feature.
        await using var ctx = await MergeTestContext.CreateAsync();
        await ctx.AddFeatureUsingDroppedRepositoryAsync("feat-keep-repo");

        await using var scope = ctx.CreateScope();
        var repo = ctx.MergeRepo(scope);
        var db = ctx.Db(scope);

        var fetched = new List<Repository> { ctx.FetchedKeptRepository() };

        var result = await repo.MergeRepositoriesAsync(fetched);

        var kept = Assert.Single(result.KeptMissingFromProvider);
        Assert.Equal(ctx.DroppedRepositoryId, kept.RepositoryId);
        Assert.Equal(new[] { "feat-keep-repo" }, kept.FeatureNames);

        Assert.True(await db.Repositories.AnyAsync(r => r.RepositoryId == ctx.DroppedRepositoryId));
        Assert.True(await db.WorkspaceRepositories.AnyAsync(l => l.RepositoryId == ctx.DroppedRepositoryId));
        Assert.True(await db.WorkspaceFeatureRepositories.AnyAsync(wfr => wfr.WorkspaceFeatureContextId == ctx.FeatureContextId));
        Assert.True(await db.WorkspaceFeatures.AnyAsync(f => f.WorkspaceFeatureId == ctx.FeatureId));

        Assert.True(await db.Repositories.AnyAsync(r => r.RepositoryId == ctx.KeptRepositoryId));
    }

    [Fact]
    public async Task DeleteAsync_of_a_connector_no_workspace_uses_deletes_the_connector_and_its_repositories()
    {
        await using var ctx = await MergeTestContext.CreateAsync();
        var otherConnectorId = await ctx.AddConnectorAsync("github-unused");
        var unlinkedId = await ctx.AddRepositoryAsync(otherConnectorId, "unused-repo", linked: false);
        await using var scope = ctx.CreateScope();
        var db = ctx.Db(scope);

        await ctx.ConnectorRepo(scope).DeleteAsync(otherConnectorId);

        Assert.False(await db.Connectors.AnyAsync(c => c.ConnectorId == otherConnectorId));
        Assert.False(await db.Repositories.AnyAsync(r => r.RepositoryId == unlinkedId));
        // The other connector and its Workspace repositories are untouched.
        Assert.True(await db.WorkspaceRepositories.AnyAsync(l => l.RepositoryId == ctx.KeptRepositoryId));
    }

    [Fact]
    public async Task DeleteAsync_keeps_workspace_repositories_and_feature_rows_and_leaves_a_removed_connector()
    {
        // Removing a connector used to cascade to its repositories and their Workspace links (or refuse when a
        // Feature used one). Connector state never removes a Workspace's repository: the delete succeeds, the
        // linked repositories stay with every dependent row, and only the catalog row no Workspace links goes.
        await using var ctx = await MergeTestContext.CreateAsync();
        await ctx.AddFeatureUsingDroppedRepositoryAsync("feat-survives-delete");
        var unlinkedId = await ctx.AddRepositoryAsync(ctx.ConnectorId, "catalog-only", linked: false);
        await ctx.SetConnectorTokenAsync(ctx.ConnectorId, "secret-token");

        await using var scope = ctx.CreateScope();
        var connectorRepository = ctx.ConnectorRepo(scope);
        var db = ctx.Db(scope);

        await connectorRepository.DeleteAsync(ctx.ConnectorId);

        Assert.True(await db.WorkspaceRepositories.AnyAsync(l => l.RepositoryId == ctx.KeptRepositoryId));
        Assert.True(await db.WorkspaceRepositories.AnyAsync(l => l.RepositoryId == ctx.DroppedRepositoryId));
        Assert.True(await db.WorkspaceFeatureRepositories.AnyAsync(wfr => wfr.WorkspaceFeatureContextId == ctx.FeatureContextId));
        Assert.False(await db.Repositories.AnyAsync(r => r.RepositoryId == unlinkedId));

        var removed = await db.Connectors.AsNoTracking().SingleAsync(c => c.ConnectorId == ctx.ConnectorId);
        Assert.NotNull(removed.RemovedAtUtc);
        Assert.Null(removed.UserToken);
        Assert.False(removed.IsActive);
        Assert.False(removed.IsHealthy);

        // A removed connector is invisible, and its name is free again.
        Assert.Empty(await connectorRepository.GetAllAsync());
        Assert.Empty(await connectorRepository.GetActiveAsync());
        Assert.Null(await connectorRepository.GetByIdAsync(ctx.ConnectorId));
        Assert.Null(await connectorRepository.GetByNameAsync("github-prod"));
        var again = await connectorRepository.AddAsync(new Connector { ConnectorName = "github-prod", ConnectorType = ConnectorType.GitHub, ApiBaseUrl = "https://api.github.com" });
        Assert.NotEqual(ctx.ConnectorId, again.ConnectorId);
    }

    [Fact]
    public async Task Refresh_moves_the_repositories_of_a_removed_connector_to_the_connector_that_returns_them()
    {
        // Reassignment is automatic: a connector that returns the same clone URL takes the repository over with
        // its links and Feature rows, and the removed connector disappears with its last repository.
        await using var ctx = await MergeTestContext.CreateAsync();
        await ctx.AddFeatureUsingDroppedRepositoryAsync("feat-moves");
        await using (var deleteScope = ctx.CreateScope())
            await ctx.ConnectorRepo(deleteScope).DeleteAsync(ctx.ConnectorId);
        var newConnectorId = await ctx.AddConnectorAsync("github-new");

        await using var scope = ctx.CreateScope();
        var db = ctx.Db(scope);
        var stored = await db.Repositories.AsNoTracking().OrderBy(r => r.RepositoryId).ToListAsync();
        Repository Fetched(Repository r, long providerId) => new()
        {
            ConnectorId = newConnectorId, RepositoryName = r.RepositoryName, OrgName = r.OrgName, Visibility = "Public", CloneUrl = r.CloneUrl, GitHubRepositoryId = providerId
        };

        // First refresh returns only one of the two: the removed connector still holds the other.
        await ctx.MergeRepo(scope).MergeRepositoriesAsync([Fetched(stored[0], 1)]);
        Assert.Equal(newConnectorId, (await db.Repositories.AsNoTracking().SingleAsync(r => r.RepositoryId == stored[0].RepositoryId)).ConnectorId);
        Assert.Equal(ctx.ConnectorId, (await db.Repositories.AsNoTracking().SingleAsync(r => r.RepositoryId == stored[1].RepositoryId)).ConnectorId);
        Assert.True(await db.Connectors.AnyAsync(c => c.ConnectorId == ctx.ConnectorId));

        await ctx.MergeRepo(scope).MergeRepositoriesAsync([Fetched(stored[0], 1), Fetched(stored[1], 2)]);

        Assert.All(await db.Repositories.AsNoTracking().ToListAsync(), r => Assert.Equal(newConnectorId, r.ConnectorId));
        Assert.Equal(2, await db.WorkspaceRepositories.CountAsync());
        Assert.True(await db.WorkspaceFeatureRepositories.AnyAsync(wfr => wfr.WorkspaceFeatureContextId == ctx.FeatureContextId));
        Assert.False(await db.Connectors.AnyAsync(c => c.ConnectorId == ctx.ConnectorId));
    }

    [Fact]
    public async Task Refresh_deletes_a_repository_of_a_removed_connector_once_no_workspace_links_it()
    {
        await using var ctx = await MergeTestContext.CreateAsync();
        await using (var deleteScope = ctx.CreateScope())
            await ctx.ConnectorRepo(deleteScope).DeleteAsync(ctx.ConnectorId);
        var newConnectorId = await ctx.AddConnectorAsync("github-new");
        await using var scope = ctx.CreateScope();
        var db = ctx.Db(scope);
        await db.WorkspaceRepositories.Where(l => l.RepositoryId == ctx.DroppedRepositoryId).ExecuteDeleteAsync();

        await ctx.MergeRepo(scope).MergeRepositoriesAsync(
            [new Repository { ConnectorId = newConnectorId, RepositoryName = "unrelated", OrgName = "acme", Visibility = "Public", CloneUrl = "https://example/unrelated.git" }]);

        Assert.False(await db.Repositories.AnyAsync(r => r.RepositoryId == ctx.DroppedRepositoryId));
        Assert.True(await db.Repositories.AnyAsync(r => r.RepositoryId == ctx.KeptRepositoryId));
        Assert.True(await db.Connectors.AnyAsync(c => c.ConnectorId == ctx.ConnectorId));
    }

    [Fact]
    public async Task Work_that_needs_a_token_is_refused_for_a_repository_whose_connector_was_removed()
    {
        await using var ctx = await MergeTestContext.CreateAsync();
        await using (var deleteScope = ctx.CreateScope())
            await ctx.ConnectorRepo(deleteScope).DeleteAsync(ctx.ConnectorId);
        await using var scope = ctx.CreateScope();
        var health = new ConnectorHealthService(ctx.Db(scope), NullLogger<ConnectorHealthService>.Instance);

        var single = await Assert.ThrowsAsync<ConnectorHealthException>(() => health.EnsureConnectorHealthyForRepositoryAsync(ctx.KeptRepositoryId));
        Assert.Equal("Repository repo-kept has no valid connector. Add or repair a connector that can access it, then fetch repositories.", single.Message);
        await Assert.ThrowsAsync<ConnectorHealthException>(() => health.EnsureConnectorsHealthyForRepositoriesAsync([ctx.KeptRepositoryId]));
        // A removed connector is not an "unhealthy connector to fix on the Connectors page".
        Assert.False(await health.AnyUsedConnectorUnhealthyAsync());
    }
}

/// <summary>
/// In-memory SQLite DI context seeding one connector with two repositories (one that the next refresh
/// keeps, one that it no longer returns) linked into one Workspace, with no Features by default.
/// <see cref="AddFeatureUsingDroppedRepositoryAsync"/> adds a Feature that uses the dropped repository.
/// </summary>
internal sealed class MergeTestContext : IAsyncDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _provider;

    public int ConnectorId { get; private set; }
    public int WorkspaceId { get; private set; }
    public int KeptRepositoryId { get; private set; }
    public int DroppedRepositoryId { get; private set; }
    public int FeatureId { get; private set; }
    public int FeatureContextId { get; private set; }

    private string _keptCloneUrl = string.Empty;
    private string _keptRepositoryName = string.Empty;
    private string _keptOrgName = string.Empty;

    private MergeTestContext(SqliteConnection connection, ServiceProvider provider)
    {
        _connection = connection;
        _provider = provider;
    }

    public static async Task<MergeTestContext> CreateAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddDbContext<AppDbContext>(o => o.UseSqlite(connection), ServiceLifetime.Scoped);
        services.AddSingleton<IWorkspaceGitChangesNotifier, RecordingGitChangesNotifier>();
        services.AddScoped<GitHubRepositoryRepository>();
        services.AddScoped<ConnectorRepository>();

        var provider = services.BuildServiceProvider();
        var ctx = new MergeTestContext(connection, provider);
        await ctx.SeedAsync();
        return ctx;
    }

    private async Task SeedAsync()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.CreateSchemaAsync();

        var connector = new Connector
        {
            ConnectorName = "github-prod",
            ConnectorType = ConnectorType.GitHub,
            ApiBaseUrl = "https://api.github.com",
            IsActive = true,
            IsHealthy = true,
        };
        db.Connectors.Add(connector);
        await db.SaveChangesAsync();

        var keptRepository = new Repository
        {
            ConnectorId = connector.ConnectorId,
            RepositoryName = "repo-kept",
            OrgName = "acme",
            Visibility = "Public",
            CloneUrl = "https://example/repo-kept.git",
        };
        var droppedRepository = new Repository
        {
            ConnectorId = connector.ConnectorId,
            RepositoryName = "repo-dropped",
            OrgName = "acme",
            Visibility = "Public",
            CloneUrl = "https://example/repo-dropped.git",
        };
        db.Repositories.AddRange(keptRepository, droppedRepository);
        await db.SaveChangesAsync();

        var workspace = new Workspace { Name = "merge-test-ws" };
        db.Workspaces.Add(workspace);
        await db.SaveChangesAsync();

        var keptLink = new WorkspaceRepositoryLink { WorkspaceId = workspace.WorkspaceId, RepositoryId = keptRepository.RepositoryId, GitVersion = "1.0.0" };
        var droppedLink = new WorkspaceRepositoryLink { WorkspaceId = workspace.WorkspaceId, RepositoryId = droppedRepository.RepositoryId, GitVersion = "1.0.0" };
        db.WorkspaceRepositories.AddRange(keptLink, droppedLink);
        await db.SaveChangesAsync();

        ConnectorId = connector.ConnectorId;
        WorkspaceId = workspace.WorkspaceId;
        KeptRepositoryId = keptRepository.RepositoryId;
        DroppedRepositoryId = droppedRepository.RepositoryId;
        _keptCloneUrl = keptRepository.CloneUrl;
        _keptRepositoryName = keptRepository.RepositoryName;
        _keptOrgName = keptRepository.OrgName!;
    }

    /// <summary>Adds a Feature whose only repository is the dropped repository's WorkspaceRepositoryLink.</summary>
    public async Task AddFeatureUsingDroppedRepositoryAsync(string featureName)
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var droppedLink = await db.WorkspaceRepositories
            .AsNoTracking()
            .SingleAsync(l => l.WorkspaceId == WorkspaceId && l.RepositoryId == DroppedRepositoryId);

        var feature = new WorkspaceFeature
        {
            WorkspaceId = WorkspaceId,
            Name = featureName,
            LifecycleState = WorkspaceFeatureLifecycleState.Ready,
            BaseKind = WorkspaceFeatureBaseKind.CurrentWorkspace,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        db.WorkspaceFeatures.Add(feature);
        await db.SaveChangesAsync();

        var featureContext = new WorkspaceFeatureContext
        {
            WorkspaceId = WorkspaceId,
            Kind = WorkspaceFeatureContextKind.Feature,
            WorkspaceFeatureId = feature.WorkspaceFeatureId,
            CreatedAt = DateTime.UtcNow,
            IsInSync = true,
        };
        db.WorkspaceFeatureContexts.Add(featureContext);
        await db.SaveChangesAsync();

        db.WorkspaceFeatureRepositories.Add(new WorkspaceFeatureRepository
        {
            WorkspaceFeatureContextId = featureContext.WorkspaceFeatureContextId,
            WorkspaceRepositoryId = droppedLink.WorkspaceRepositoryId,
            WorktreePath = $@"C:\gm-test-root\.graymoon\merge-test-ws\features\{featureName}\repo-dropped",
            State = WorkspaceFeatureRepositoryState.Ready,
            BaseCommitSha = "abc123",
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        FeatureId = feature.WorkspaceFeatureId;
        FeatureContextId = featureContext.WorkspaceFeatureContextId;
    }

    public async Task SetConnectorTokenAsync(int connectorId, string token)
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        (await db.Connectors.SingleAsync(c => c.ConnectorId == connectorId)).UserToken = token;
        await db.SaveChangesAsync();
    }
    public async Task<int> AddConnectorAsync(string name)
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var connector = new Connector
        {
            ConnectorName = name,
            ConnectorType = ConnectorType.GitHub,
            ApiBaseUrl = "https://api.github.com",
            IsActive = true,
            IsHealthy = true,
        };
        db.Connectors.Add(connector);
        await db.SaveChangesAsync();
        return connector.ConnectorId;
    }

    /// <summary>Adds a repository the next refresh does not return; <paramref name="linked"/> links it into the seeded Workspace.</summary>
    public async Task<int> AddRepositoryAsync(int connectorId, string name, bool linked)
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var repository = new Repository
        {
            ConnectorId = connectorId,
            RepositoryName = name,
            OrgName = "other-org",
            Visibility = "Public",
            CloneUrl = $"https://other.example/{name}.git",
        };
        db.Repositories.Add(repository);
        await db.SaveChangesAsync();
        if (linked)
        {
            db.WorkspaceRepositories.Add(new WorkspaceRepositoryLink { WorkspaceId = WorkspaceId, RepositoryId = repository.RepositoryId, GitVersion = "1.0.0" });
            await db.SaveChangesAsync();
        }

        return repository.RepositoryId;
    }

    /// <summary>An unattached Repository matching the kept repository's CloneUrl, as a connector refresh would fetch it again.</summary>
    public Repository FetchedKeptRepository() => new()
    {
        ConnectorId = ConnectorId,
        RepositoryName = _keptRepositoryName,
        OrgName = _keptOrgName,
        Visibility = "Public",
        CloneUrl = _keptCloneUrl,
    };

    public AsyncServiceScope CreateScope() => _provider.CreateAsyncScope();

    public AppDbContext Db(AsyncServiceScope scope) => scope.ServiceProvider.GetRequiredService<AppDbContext>();

    public GitHubRepositoryRepository MergeRepo(AsyncServiceScope scope) => scope.ServiceProvider.GetRequiredService<GitHubRepositoryRepository>();

    public ConnectorRepository ConnectorRepo(AsyncServiceScope scope) => scope.ServiceProvider.GetRequiredService<ConnectorRepository>();

    public async ValueTask DisposeAsync()
    {
        await _provider.DisposeAsync();
        await _connection.DisposeAsync();
    }
}
