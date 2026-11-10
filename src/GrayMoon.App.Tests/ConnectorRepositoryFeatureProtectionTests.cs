using GrayMoon.App.Data;
using GrayMoon.App.Models;
using GrayMoon.App.Repositories;
using GrayMoon.App.Services.GitChanges;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

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
    public async Task DeleteAsync_no_features_deletes_connector_and_repositories_as_today()
    {
        // Regression guard R-B5 (A4 step 0): characterization of today's behaviour on a database with no
        // Features. Deleting a connector with no Feature usage still deletes the connector and every one
        // of its repositories, exactly as before.
        await using var ctx = await MergeTestContext.CreateAsync();
        await using var scope = ctx.CreateScope();
        var connectorRepository = ctx.ConnectorRepo(scope);
        var db = ctx.Db(scope);

        await connectorRepository.DeleteAsync(ctx.ConnectorId);

        Assert.False(await db.Connectors.AnyAsync(c => c.ConnectorId == ctx.ConnectorId));
        Assert.False(await db.Repositories.AnyAsync(r => r.RepositoryId == ctx.KeptRepositoryId));
        Assert.False(await db.Repositories.AnyAsync(r => r.RepositoryId == ctx.DroppedRepositoryId));
    }

    [Fact]
    public async Task DeleteAsync_refuses_and_deletes_nothing_when_a_repository_is_used_by_a_feature()
    {
        // Step 3: delete refuses outright when any repository of the connector has a
        // WorkspaceFeatureRepositories row, and nothing is deleted.
        await using var ctx = await MergeTestContext.CreateAsync();
        await ctx.AddFeatureUsingDroppedRepositoryAsync("feat-blocks-delete");

        await using var scope = ctx.CreateScope();
        var connectorRepository = ctx.ConnectorRepo(scope);
        var db = ctx.Db(scope);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => connectorRepository.DeleteAsync(ctx.ConnectorId));
        Assert.Equal("Remove the Features that use this connector's repositories first.", ex.Message);

        Assert.True(await db.Connectors.AnyAsync(c => c.ConnectorId == ctx.ConnectorId));
        Assert.True(await db.Repositories.AnyAsync(r => r.RepositoryId == ctx.KeptRepositoryId));
        Assert.True(await db.Repositories.AnyAsync(r => r.RepositoryId == ctx.DroppedRepositoryId));
        Assert.True(await db.WorkspaceFeatureRepositories.AnyAsync(wfr => wfr.WorkspaceFeatureContextId == ctx.FeatureContextId));
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
