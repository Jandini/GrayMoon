using GrayMoon.App.Data;
using GrayMoon.App.Models;
using GrayMoon.App.Repositories;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace GrayMoon.App.Tests;

/// <summary>
/// Covers virtual/generated NuGet package dependencies: a consumer repository's real .csproj references a package
/// with no physical package-producing .csproj, inferred from a configured .csproj version-file pattern.
/// </summary>
public sealed class WorkspaceProjectRepositoryGeneratedPackageTests
{
    private const string PackageName = "GrayMoon.Generated.Package";

    [Fact]
    public async Task SyncGeneratedPackageDependenciesAsync_creates_generated_project_and_edge()
    {
        await using var ctx = await GeneratedPackageTestContext.CreateAsync();
        await using var scope = ctx.CreateScope();
        await ctx.Repo(scope).SyncGeneratedPackageDependenciesAsync(ctx.WorkspaceId, [ctx.MakeInfo()]);

        var db = ctx.Db(scope);
        var generated = await db.WorkspaceProjects.SingleAsync(p => p.IsGenerated);
        Assert.Equal(ctx.ProducerRepositoryId, generated.RepositoryId);
        Assert.Equal(PackageName, generated.PackageId);
        Assert.Equal(ProjectType.Package, generated.ProjectType);

        var edge = await db.ProjectDependencies.SingleAsync();
        Assert.Equal(ctx.ConsumerProjectId, edge.DependentProjectId);
        Assert.Equal(generated.ProjectId, edge.ReferencedProjectId);
    }

    [Fact]
    public async Task SyncGeneratedPackageDependenciesAsync_is_idempotent()
    {
        await using var ctx = await GeneratedPackageTestContext.CreateAsync();
        await using var scope = ctx.CreateScope();
        var repo = ctx.Repo(scope);
        await repo.SyncGeneratedPackageDependenciesAsync(ctx.WorkspaceId, [ctx.MakeInfo()]);
        await repo.SyncGeneratedPackageDependenciesAsync(ctx.WorkspaceId, [ctx.MakeInfo()]);

        var db = ctx.Db(scope);
        Assert.Equal(1, await db.WorkspaceProjects.CountAsync(p => p.IsGenerated));
        Assert.Equal(1, await db.ProjectDependencies.CountAsync());
    }

    [Fact]
    public async Task SyncGeneratedPackageDependenciesAsync_removes_stale_rows_when_config_removed()
    {
        await using var ctx = await GeneratedPackageTestContext.CreateAsync();
        await using var scope = ctx.CreateScope();
        var repo = ctx.Repo(scope);
        await repo.SyncGeneratedPackageDependenciesAsync(ctx.WorkspaceId, [ctx.MakeInfo()]);

        // Config removed/edited: resolved set is now empty.
        await repo.SyncGeneratedPackageDependenciesAsync(ctx.WorkspaceId, []);

        var db = ctx.Db(scope);
        Assert.Equal(0, await db.WorkspaceProjects.CountAsync(p => p.IsGenerated));
        Assert.Equal(0, await db.ProjectDependencies.CountAsync());
    }

    [Fact]
    public async Task MergeWorkspaceProjectsAsync_does_not_delete_generated_rows_for_producer_repo()
    {
        await using var ctx = await GeneratedPackageTestContext.CreateAsync();
        await using var scope = ctx.CreateScope();
        var repo = ctx.Repo(scope);
        await repo.SyncGeneratedPackageDependenciesAsync(ctx.WorkspaceId, [ctx.MakeInfo()]);

        // Producer repo's own (unrelated) project sync must not remove the generated package row it "produces".
        await repo.MergeWorkspaceProjectsAsync(ctx.WorkspaceId, ctx.ProducerRepositoryId,
            [new SyncProjectInfo("Producer.Lib", ProjectType.Library, "src/Producer/Producer.csproj", "net10.0", null, [])]);

        var db = ctx.Db(scope);
        Assert.Equal(1, await db.WorkspaceProjects.CountAsync(p => p.IsGenerated && p.PackageId == PackageName));
        Assert.Equal(1, await db.WorkspaceProjects.CountAsync(p => !p.IsGenerated && p.RepositoryId == ctx.ProducerRepositoryId));
    }

    [Fact]
    public async Task RecomputeAndPersistRepositoryDependencyStatsAsync_orders_consumer_after_producer()
    {
        await using var ctx = await GeneratedPackageTestContext.CreateAsync();
        await using var scope = ctx.CreateScope();
        var repo = ctx.Repo(scope);
        await repo.SyncGeneratedPackageDependenciesAsync(ctx.WorkspaceId, [ctx.MakeInfo()]);
        await repo.RecomputeAndPersistRepositoryDependencyStatsAsync(ctx.WorkspaceId);

        var db = ctx.Db(scope);
        var links = await db.WorkspaceRepositories.Where(wr => wr.WorkspaceId == ctx.WorkspaceId).ToListAsync();
        var producerLevel = links.Single(l => l.RepositoryId == ctx.ProducerRepositoryId).DependencyLevel;
        var consumerLevel = links.Single(l => l.RepositoryId == ctx.ConsumerRepositoryId).DependencyLevel;

        Assert.NotNull(producerLevel);
        Assert.NotNull(consumerLevel);
        Assert.True(consumerLevel > producerLevel, "Consumer must be ordered at a level strictly after the virtual package's producer.");
    }

    [Fact]
    public async Task GetImplicitReferencedRepoIdsBySourceAsync_reports_generated_package_edge_as_FromPackage()
    {
        await using var ctx = await GeneratedPackageTestContext.CreateAsync();
        await using var scope = ctx.CreateScope();
        var repo = ctx.Repo(scope);
        await repo.SyncGeneratedPackageDependenciesAsync(ctx.WorkspaceId, [ctx.MakeInfo()]);

        var bySource = await repo.GetImplicitReferencedRepoIdsBySourceAsync(ctx.WorkspaceId, ctx.ConsumerRepositoryId);

        Assert.Contains(ctx.ProducerRepositoryId, bySource.FromPackage);
        Assert.DoesNotContain(ctx.ProducerRepositoryId, bySource.FromProject);
        Assert.DoesNotContain(ctx.ProducerRepositoryId, bySource.FromFile);
    }

    [Fact]
    public async Task SyncGeneratedPackageDependenciesAsync_joins_consumer_project_when_path_separators_differ()
    {
        await using var ctx = await GeneratedPackageTestContext.CreateAsync();
        await using var scope = ctx.CreateScope();
        await ctx.Repo(scope).SyncGeneratedPackageDependenciesAsync(
            ctx.WorkspaceId,
            [ctx.MakeInfo(consumerProjectFilePath: @"src\Consumer\Consumer.csproj", version: "1.2.3")]);

        var db = ctx.Db(scope);
        var generated = await db.WorkspaceProjects.SingleAsync(p => p.IsGenerated);
        var edge = await db.ProjectDependencies.SingleAsync();
        Assert.Equal(ctx.ConsumerProjectId, edge.DependentProjectId);
        Assert.Equal(generated.ProjectId, edge.ReferencedProjectId);
        Assert.Equal("1.2.3", edge.Version);
    }

    [Fact]
    public async Task SyncGeneratedPackageDependenciesAsync_persists_and_updates_edge_version()
    {
        await using var ctx = await GeneratedPackageTestContext.CreateAsync();
        await using var scope = ctx.CreateScope();
        var repo = ctx.Repo(scope);

        await repo.SyncGeneratedPackageDependenciesAsync(ctx.WorkspaceId, [ctx.MakeInfo(version: "1.0.0")]);
        var first = await ctx.Db(scope).ProjectDependencies.SingleAsync();
        Assert.Equal("1.0.0", first.Version);

        await repo.SyncGeneratedPackageDependenciesAsync(ctx.WorkspaceId, [ctx.MakeInfo(version: "2.0.0")]);
        var updated = await ctx.Db(scope).ProjectDependencies.SingleAsync();
        Assert.Equal("2.0.0", updated.Version);
        Assert.Equal(1, await ctx.Db(scope).ProjectDependencies.CountAsync());
    }

    [Fact]
    public async Task GetPushPlanPayloadAsync_includes_generated_package_version_on_consumer()
    {
        await using var ctx = await GeneratedPackageTestContext.CreateAsync();
        await using var scope = ctx.CreateScope();
        var repo = ctx.Repo(scope);
        var db = ctx.Db(scope);

        var nugetConnector = new Connector
        {
            ConnectorName = "nuget-prod",
            ConnectorType = ConnectorType.NuGet,
            ApiBaseUrl = "https://api.nuget.org/v3/index.json",
            IsActive = true,
            IsHealthy = true,
        };
        db.Connectors.Add(nugetConnector);
        await db.SaveChangesAsync();

        var links = await db.WorkspaceRepositories.Where(wr => wr.WorkspaceId == ctx.WorkspaceId).ToListAsync();
        links.Single(l => l.RepositoryId == ctx.ProducerRepositoryId).DependencyLevel = 1;
        links.Single(l => l.RepositoryId == ctx.ConsumerRepositoryId).DependencyLevel = 2;
        await db.SaveChangesAsync();

        await repo.SyncGeneratedPackageDependenciesAsync(ctx.WorkspaceId, [ctx.MakeInfo(version: "3.4.5")]);

        var generated = await db.WorkspaceProjects.SingleAsync(p => p.IsGenerated);
        generated.MatchedConnectorId = nugetConnector.ConnectorId;
        await db.SaveChangesAsync();

        var plan = await repo.GetPushPlanPayloadAsync(ctx.WorkspaceId);
        var consumer = plan.Single(p => p.RepoId == ctx.ConsumerRepositoryId);
        var required = Assert.Single(consumer.RequiredPackages);
        Assert.Equal(PackageName, required.PackageId);
        Assert.Equal("3.4.5", required.Version);
        Assert.Equal(nugetConnector.ConnectorId, required.MatchedConnectorId);
    }
}

/// <summary>In-memory SQLite DI context seeded with a producer repo (no physical project) and a consumer repo with one real .csproj-backed project, for generated-package tests.</summary>
public sealed class GeneratedPackageTestContext : IAsyncDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _provider;

    public const string ConsumerProjectFilePath = "src/Consumer/Consumer.csproj";
    public const string PackageName = "GrayMoon.Generated.Package";

    public int WorkspaceId { get; private set; }
    public int ProducerRepositoryId { get; private set; }
    public int ConsumerRepositoryId { get; private set; }
    public int ConsumerProjectId { get; private set; }

    private GeneratedPackageTestContext(SqliteConnection connection, ServiceProvider provider)
    {
        _connection = connection;
        _provider = provider;
    }

    public static async Task<GeneratedPackageTestContext> CreateAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddDbContext<AppDbContext>(o => o.UseSqlite(connection), ServiceLifetime.Scoped);
        services.AddDbContextFactory<AppDbContext>(o => o.UseSqlite(connection), ServiceLifetime.Singleton);
        services.AddScoped<WorkspaceFileVersionConfigRepository>();
        services.AddScoped<WorkspaceRepositoryCustomDependencyRepository>();
        services.AddScoped<WorkspaceProjectRepository>();

        var provider = services.BuildServiceProvider();
        var ctx = new GeneratedPackageTestContext(connection, provider);
        await ctx.SeedAsync();
        return ctx;
    }

    private async Task SeedAsync()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.EnsureCreatedAsync();

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

        var producer = new Repository { ConnectorId = connector.ConnectorId, RepositoryName = "producer-repo", OrgName = "acme", Visibility = "Public", CloneUrl = "https://example/producer.git" };
        var consumer = new Repository { ConnectorId = connector.ConnectorId, RepositoryName = "consumer-repo", OrgName = "acme", Visibility = "Public", CloneUrl = "https://example/consumer.git" };
        db.Repositories.AddRange(producer, consumer);
        await db.SaveChangesAsync();

        var workspace = new Workspace { Name = "generated-package-ws" };
        db.Workspaces.Add(workspace);
        await db.SaveChangesAsync();

        db.WorkspaceRepositories.AddRange(
            new WorkspaceRepositoryLink { WorkspaceId = workspace.WorkspaceId, RepositoryId = producer.RepositoryId },
            new WorkspaceRepositoryLink { WorkspaceId = workspace.WorkspaceId, RepositoryId = consumer.RepositoryId });
        await db.SaveChangesAsync();

        var consumerProject = new WorkspaceProject
        {
            WorkspaceId = workspace.WorkspaceId,
            RepositoryId = consumer.RepositoryId,
            ProjectName = "Consumer",
            ProjectType = ProjectType.Library,
            ProjectFilePath = ConsumerProjectFilePath,
            TargetFramework = "net10.0"
        };
        db.WorkspaceProjects.Add(consumerProject);
        await db.SaveChangesAsync();

        WorkspaceId = workspace.WorkspaceId;
        ProducerRepositoryId = producer.RepositoryId;
        ConsumerRepositoryId = consumer.RepositoryId;
        ConsumerProjectId = consumerProject.ProjectId;
    }

    public AsyncServiceScope CreateScope() => _provider.CreateAsyncScope();

    public WorkspaceProjectRepository Repo(AsyncServiceScope scope) => scope.ServiceProvider.GetRequiredService<WorkspaceProjectRepository>();

    public AppDbContext Db(AsyncServiceScope scope) => scope.ServiceProvider.GetRequiredService<AppDbContext>();

    public GeneratedPackageDependencyInfo MakeInfo(string? consumerProjectFilePath = null, string? version = null) =>
        new(ConsumerRepositoryId, consumerProjectFilePath ?? ConsumerProjectFilePath, ProducerRepositoryId, PackageName, version);

    public async ValueTask DisposeAsync()
    {
        await _provider.DisposeAsync();
        await _connection.DisposeAsync();
    }
}
