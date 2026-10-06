using GrayMoon.Abstractions.Worker;
using GrayMoon.App.Data;
using GrayMoon.App.Models;
using GrayMoon.App.Repositories;
using GrayMoon.App.Services;
using GrayMoon.App.Services.Application;
using GrayMoon.App.Services.GitChanges;
using GrayMoon.App.Services.Workspaces;
using GrayMoon.Application;
using GrayMoon.Application.Features;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GrayMoon.App.Tests;

/// <summary>
/// B4: covers the remaining context leaks named in the unit text - push plan tag exclusion (item 1), the
/// notification panel's "repos that need a push" check (item 2), and the Files/Packages reads (item 3). Each
/// scoping test seeds the special Workspace context and one Feature context with deliberately different state,
/// then asserts a scoped read only ever sees its own context's data. Each also has a "pin the Workspace first"
/// characterization test on a database with no Features, proving the special Workspace's result is unchanged.
/// </summary>
public sealed class WorkspaceB4ContextLeakTests
{
    [Fact]
    public async Task GetPushPlanPayloadAsync_excludes_repo_tag_pinned_in_feature_context_only()
    {
        await using var ctx = await B4ContextLeakTestContext.CreateAsync();
        await using var scope = ctx.CreateScope();
        var db = ctx.Db(scope);

        // Repo B is tag-pinned in the Feature's own context state, but not in the shared link.
        db.WorkspaceRepositoryContextStates.Add(new WorkspaceRepositoryContextState
        {
            WorkspaceFeatureContextId = ctx.FeatureContextId,
            WorkspaceRepositoryId = ctx.WorkspaceRepositoryBId,
            CheckedOutTag = "v1.0",
        });
        await db.SaveChangesAsync();

        var repo = ctx.ProjectRepo(scope);

        var featurePlan = await repo.GetPushPlanPayloadAsync(ctx.WorkspaceId, ctx.FeatureContextId);
        Assert.DoesNotContain(featurePlan, p => p.RepoId == ctx.RepoBId);

        var workspacePlan = await repo.GetPushPlanPayloadAsync(ctx.WorkspaceId, ctx.SpecialContextId);
        Assert.Contains(workspacePlan, p => p.RepoId == ctx.RepoBId);
    }

    [Fact]
    public async Task Characterization_GetPushPlanPayloadAsync_tag_pinned_repo_excluded_workspace_only_database_unaffected()
    {
        await using var ctx = await B4ContextLeakTestContext.CreateAsync();
        await using var scope = ctx.CreateScope();
        var db = ctx.Db(scope);

        // No Feature context state at all; the shared link itself is tag-pinned (today's only path).
        var linkB = await db.WorkspaceRepositories.SingleAsync(l => l.WorkspaceRepositoryId == ctx.WorkspaceRepositoryBId);
        linkB.CheckedOutTag = "v2.0";
        await db.SaveChangesAsync();

        var repo = ctx.ProjectRepo(scope);
        var legacy = await repo.GetPushPlanPayloadAsync(ctx.WorkspaceId);
        var scoped = await repo.GetPushPlanPayloadAsync(ctx.WorkspaceId, ctx.SpecialContextId);

        Assert.DoesNotContain(legacy, p => p.RepoId == ctx.RepoBId);
        Assert.DoesNotContain(scoped, p => p.RepoId == ctx.RepoBId);
        Assert.Equal(legacy.Select(p => p.RepoId).OrderBy(x => x), scoped.Select(p => p.RepoId).OrderBy(x => x));
    }

    [Fact]
    public async Task GetRepositoryIdsThatNeedPushForNotificationAsync_uses_feature_context_state_not_shared_link()
    {
        await using var ctx = await B4ContextLeakTestContext.CreateAsync();
        await using var scope = ctx.CreateScope();
        var db = ctx.Db(scope);

        // Shared link: no outgoing commits (Workspace does not need a push). Feature's own context state for
        // the same repo: 5 outgoing commits (the Feature does need a push).
        db.WorkspaceRepositoryContextStates.Add(new WorkspaceRepositoryContextState
        {
            WorkspaceFeatureContextId = ctx.FeatureContextId,
            WorkspaceRepositoryId = ctx.WorkspaceRepositoryAId,
            OutgoingCommits = 5,
        });
        await db.SaveChangesAsync();

        var workspaceRepo = ctx.WorkspaceRepo(scope);
        var repoIds = new HashSet<int> { ctx.RepoAId };

        var featureResult = await workspaceRepo.GetRepositoryIdsThatNeedPushForNotificationAsync(
            ctx.WorkspaceId, ctx.FeatureContextId, repoIds, maxLevel: null, includeNeverPushedUpstream: false);
        Assert.Contains(ctx.RepoAId, featureResult);

        var workspaceResult = await workspaceRepo.GetRepositoryIdsThatNeedPushForNotificationAsync(
            ctx.WorkspaceId, ctx.SpecialContextId, repoIds, maxLevel: null, includeNeverPushedUpstream: false);
        Assert.DoesNotContain(ctx.RepoAId, workspaceResult);
    }

    [Fact]
    public async Task GetRepositoryIdsThatNeedPushForNotificationAsync_feature_with_no_context_state_is_not_included()
    {
        // Rule 2 of feature-context-scoping.mdc: a Feature with no context-state row yet is "unknown", never
        // the Workspace's own value.
        await using var ctx = await B4ContextLeakTestContext.CreateAsync();
        await using var scope = ctx.CreateScope();
        var db = ctx.Db(scope);

        var linkA = await db.WorkspaceRepositories.SingleAsync(l => l.WorkspaceRepositoryId == ctx.WorkspaceRepositoryAId);
        linkA.OutgoingCommits = 5;
        await db.SaveChangesAsync();

        var workspaceRepo = ctx.WorkspaceRepo(scope);
        var featureResult = await workspaceRepo.GetRepositoryIdsThatNeedPushForNotificationAsync(
            ctx.WorkspaceId, ctx.FeatureContextId, new HashSet<int> { ctx.RepoAId }, maxLevel: null, includeNeverPushedUpstream: false);

        Assert.Empty(featureResult);
    }

    [Fact]
    public async Task GetRepositoryIdsThatNeedPushForNotificationAsync_includeNeverPushedUpstream_true_matches_push_badge_predicate()
    {
        await using var ctx = await B4ContextLeakTestContext.CreateAsync();
        await using var scope = ctx.CreateScope();
        var db = ctx.Db(scope);

        db.WorkspaceRepositoryContextStates.Add(new WorkspaceRepositoryContextState
        {
            WorkspaceFeatureContextId = ctx.FeatureContextId,
            WorkspaceRepositoryId = ctx.WorkspaceRepositoryAId,
            OutgoingCommits = 0,
            BranchHasUpstream = false,
        });
        await db.SaveChangesAsync();

        var workspaceRepo = ctx.WorkspaceRepo(scope);
        var repoIds = new HashSet<int> { ctx.RepoAId };

        var withUpstreamCheck = await workspaceRepo.GetRepositoryIdsThatNeedPushForNotificationAsync(
            ctx.WorkspaceId, ctx.FeatureContextId, repoIds, maxLevel: null, includeNeverPushedUpstream: true);
        Assert.Contains(ctx.RepoAId, withUpstreamCheck);

        var withoutUpstreamCheck = await workspaceRepo.GetRepositoryIdsThatNeedPushForNotificationAsync(
            ctx.WorkspaceId, ctx.FeatureContextId, repoIds, maxLevel: null, includeNeverPushedUpstream: false);
        Assert.DoesNotContain(ctx.RepoAId, withoutUpstreamCheck);
    }

    [Fact]
    public async Task Characterization_GetRepositoryIdsThatNeedPushForNotificationAsync_workspace_only_database_unaffected()
    {
        await using var ctx = await B4ContextLeakTestContext.CreateAsync();
        await using var scope = ctx.CreateScope();
        var db = ctx.Db(scope);

        var linkA = await db.WorkspaceRepositories.SingleAsync(l => l.WorkspaceRepositoryId == ctx.WorkspaceRepositoryAId);
        linkA.OutgoingCommits = 3;
        await db.SaveChangesAsync();

        var workspaceRepo = ctx.WorkspaceRepo(scope);
        var result = await workspaceRepo.GetRepositoryIdsThatNeedPushForNotificationAsync(
            ctx.WorkspaceId, ctx.SpecialContextId, new HashSet<int> { ctx.RepoAId, ctx.RepoBId }, maxLevel: null, includeNeverPushedUpstream: false);

        Assert.Contains(ctx.RepoAId, result);
        Assert.DoesNotContain(ctx.RepoBId, result);
    }

    [Fact]
    public async Task FileOperations_ListAsync_uses_feature_context_missing_flag_not_shared_row()
    {
        await using var ctx = await B4ContextLeakTestContext.CreateAsync();
        await using var scope = ctx.CreateScope();
        var db = ctx.Db(scope);

        var file = new WorkspaceFile
        {
            WorkspaceId = ctx.WorkspaceId,
            RepositoryId = ctx.RepoAId,
            FileName = "Version.props",
            FilePath = "src/Version.props",
            IsMissingOnDisk = false,
        };
        db.WorkspaceFiles.Add(file);
        await db.SaveChangesAsync();

        db.WorkspaceFileContextStates.Add(new WorkspaceFileContextState
        {
            WorkspaceFeatureContextId = ctx.FeatureContextId,
            FileId = file.FileId,
            IsMissingOnDisk = true,
        });
        await db.SaveChangesAsync();

        var operations = ctx.FileOperations(scope);

        var featureFiles = await operations.ListAsync(ctx.WorkspaceId, new WorkspaceFeatureContextId(ctx.FeatureContextId), CancellationToken.None);
        var featureFile = Assert.Single(featureFiles!);
        Assert.True(featureFile.IsMissingOnDisk);

        var workspaceFiles = await operations.ListAsync(ctx.WorkspaceId, new WorkspaceFeatureContextId(ctx.SpecialContextId), CancellationToken.None);
        var workspaceFile = Assert.Single(workspaceFiles!);
        Assert.False(workspaceFile.IsMissingOnDisk);
    }

    [Fact]
    public async Task Characterization_FileOperations_ListAsync_workspace_only_database_unaffected()
    {
        await using var ctx = await B4ContextLeakTestContext.CreateAsync();
        await using var scope = ctx.CreateScope();
        var db = ctx.Db(scope);

        var file = new WorkspaceFile
        {
            WorkspaceId = ctx.WorkspaceId,
            RepositoryId = ctx.RepoAId,
            FileName = "Version.props",
            FilePath = "src/Version.props",
            IsMissingOnDisk = true,
        };
        db.WorkspaceFiles.Add(file);
        await db.SaveChangesAsync();

        var operations = ctx.FileOperations(scope);
        var files = await operations.ListAsync(ctx.WorkspaceId, new WorkspaceFeatureContextId(ctx.SpecialContextId), CancellationToken.None);

        var result = Assert.Single(files!);
        Assert.True(result.IsMissingOnDisk);
    }

    [Fact]
    public async Task GetPackagesByWorkspaceIdAsync_context_overload_separates_real_packages_but_keeps_generated_in_both()
    {
        await using var ctx = await B4ContextLeakTestContext.CreateAsync();
        await using var scope = ctx.CreateScope();
        var db = ctx.Db(scope);

        db.WorkspaceProjects.Add(new WorkspaceProject
        {
            WorkspaceId = ctx.WorkspaceId,
            WorkspaceFeatureContextId = ctx.SpecialContextId,
            RepositoryId = ctx.RepoBId,
            ProjectName = "Workspace.Pkg",
            ProjectType = ProjectType.Package,
            ProjectFilePath = "src/B/Workspace.Pkg.csproj",
            TargetFramework = "net10.0",
            PackageId = "Workspace.Pkg",
        });
        db.WorkspaceProjects.Add(new WorkspaceProject
        {
            WorkspaceId = ctx.WorkspaceId,
            WorkspaceFeatureContextId = ctx.FeatureContextId,
            RepositoryId = ctx.RepoBId,
            ProjectName = "Feature.Pkg",
            ProjectType = ProjectType.Package,
            ProjectFilePath = "src/B/Feature.Pkg.csproj",
            TargetFramework = "net10.0",
            PackageId = "Feature.Pkg",
        });
        db.WorkspaceProjects.Add(new WorkspaceProject
        {
            WorkspaceId = ctx.WorkspaceId,
            WorkspaceFeatureContextId = ctx.SpecialContextId,
            RepositoryId = ctx.RepoBId,
            ProjectName = "Generated.Pkg",
            ProjectType = ProjectType.Package,
            ProjectFilePath = "",
            TargetFramework = "",
            PackageId = "Generated.Pkg",
            IsGenerated = true,
        });
        await db.SaveChangesAsync();

        var repo = ctx.ProjectRepo(scope);

        var featurePackages = await repo.GetPackagesByWorkspaceIdAsync(ctx.WorkspaceId, ctx.FeatureContextId);
        Assert.Contains(featurePackages, p => p.PackageId == "Feature.Pkg");
        Assert.Contains(featurePackages, p => p.PackageId == "Generated.Pkg");
        Assert.DoesNotContain(featurePackages, p => p.PackageId == "Workspace.Pkg");

        var workspacePackages = await repo.GetPackagesByWorkspaceIdAsync(ctx.WorkspaceId, ctx.SpecialContextId);
        Assert.Contains(workspacePackages, p => p.PackageId == "Workspace.Pkg");
        Assert.Contains(workspacePackages, p => p.PackageId == "Generated.Pkg");
        Assert.DoesNotContain(workspacePackages, p => p.PackageId == "Feature.Pkg");
    }

    [Fact]
    public async Task Characterization_GetPackagesByWorkspaceIdAsync_workspace_only_database_unaffected()
    {
        await using var ctx = await B4ContextLeakTestContext.CreateAsync();
        await using var scope = ctx.CreateScope();
        var db = ctx.Db(scope);

        db.WorkspaceProjects.Add(new WorkspaceProject
        {
            WorkspaceId = ctx.WorkspaceId,
            WorkspaceFeatureContextId = ctx.SpecialContextId,
            RepositoryId = ctx.RepoBId,
            ProjectName = "Workspace.Pkg",
            ProjectType = ProjectType.Package,
            ProjectFilePath = "src/B/Workspace.Pkg.csproj",
            TargetFramework = "net10.0",
            PackageId = "Workspace.Pkg",
        });
        await db.SaveChangesAsync();

        var repo = ctx.ProjectRepo(scope);
        var legacy = await repo.GetPackagesByWorkspaceIdAsync(ctx.WorkspaceId);
        var scoped = await repo.GetPackagesByWorkspaceIdAsync(ctx.WorkspaceId, ctx.SpecialContextId);

        Assert.Equal(legacy.Select(p => p.ProjectId).OrderBy(x => x), scoped.Select(p => p.ProjectId).OrderBy(x => x));
        Assert.Single(scoped);
    }
}

/// <summary>
/// In-memory SQLite DI context seeded with two repositories (A, B) linked to one workspace, the special Workspace
/// context, and one Feature context, with no context-state rows by default (each test adds the ones it needs).
/// </summary>
public sealed class B4ContextLeakTestContext : IAsyncDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _provider;

    public int WorkspaceId { get; private set; }
    public int RepoAId { get; private set; }
    public int RepoBId { get; private set; }
    public int WorkspaceRepositoryAId { get; private set; }
    public int WorkspaceRepositoryBId { get; private set; }
    public int SpecialContextId { get; private set; }
    public int FeatureContextId { get; private set; }

    private B4ContextLeakTestContext(SqliteConnection connection, ServiceProvider provider)
    {
        _connection = connection;
        _provider = provider;
    }

    private sealed class NoOpWorkerBridge : IWorkerBridge
    {
        public bool IsWorkerConnected => false;

        public Task<WorkerCommandResponse> SendCommandAsync(string command, object args, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Not used by these tests.");
    }

    public static async Task<B4ContextLeakTestContext> CreateAsync()
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
        services.AddScoped<WorkspaceFileRepository>();

        var provider = services.BuildServiceProvider();
        var ctx = new B4ContextLeakTestContext(connection, provider);
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

        var repoA = new Repository { ConnectorId = connector.ConnectorId, RepositoryName = "repo-a", OrgName = "acme", Visibility = "Public", CloneUrl = "https://example/repo-a.git" };
        var repoB = new Repository { ConnectorId = connector.ConnectorId, RepositoryName = "repo-b", OrgName = "acme", Visibility = "Public", CloneUrl = "https://example/repo-b.git" };
        db.Repositories.AddRange(repoA, repoB);
        await db.SaveChangesAsync();

        var workspace = new Workspace { Name = "b4-context-leak-ws" };
        db.Workspaces.Add(workspace);
        await db.SaveChangesAsync();

        var linkA = new WorkspaceRepositoryLink { WorkspaceId = workspace.WorkspaceId, RepositoryId = repoA.RepositoryId };
        var linkB = new WorkspaceRepositoryLink { WorkspaceId = workspace.WorkspaceId, RepositoryId = repoB.RepositoryId };
        db.WorkspaceRepositories.AddRange(linkA, linkB);
        await db.SaveChangesAsync();

        var specialContext = new WorkspaceFeatureContext
        {
            WorkspaceId = workspace.WorkspaceId,
            Kind = WorkspaceFeatureContextKind.Workspace,
            CreatedAt = DateTime.UtcNow,
            IsInSync = true,
        };
        var feature = new WorkspaceFeature
        {
            WorkspaceId = workspace.WorkspaceId,
            Name = "feature-b4",
            LifecycleState = WorkspaceFeatureLifecycleState.Ready,
            BaseKind = WorkspaceFeatureBaseKind.CurrentWorkspace,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        db.WorkspaceFeatureContexts.Add(specialContext);
        db.WorkspaceFeatures.Add(feature);
        await db.SaveChangesAsync();

        var featureContext = new WorkspaceFeatureContext
        {
            WorkspaceId = workspace.WorkspaceId,
            Kind = WorkspaceFeatureContextKind.Feature,
            WorkspaceFeatureId = feature.WorkspaceFeatureId,
            CreatedAt = DateTime.UtcNow,
            IsInSync = true,
        };
        db.WorkspaceFeatureContexts.Add(featureContext);
        await db.SaveChangesAsync();

        WorkspaceId = workspace.WorkspaceId;
        RepoAId = repoA.RepositoryId;
        RepoBId = repoB.RepositoryId;
        WorkspaceRepositoryAId = linkA.WorkspaceRepositoryId;
        WorkspaceRepositoryBId = linkB.WorkspaceRepositoryId;
        SpecialContextId = specialContext.WorkspaceFeatureContextId;
        FeatureContextId = featureContext.WorkspaceFeatureContextId;
    }

    public AsyncServiceScope CreateScope() => _provider.CreateAsyncScope();

    public AppDbContext Db(AsyncServiceScope scope) => scope.ServiceProvider.GetRequiredService<AppDbContext>();

    public WorkspaceProjectRepository ProjectRepo(AsyncServiceScope scope) => scope.ServiceProvider.GetRequiredService<WorkspaceProjectRepository>();

    public WorkspaceRepository WorkspaceRepo(AsyncServiceScope scope)
    {
        var db = Db(scope);
        var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
        var workspaceService = new WorkspaceService(
            new NoOpWorkerBridge(),
            NullLogger<WorkspaceService>.Instance,
            new AppSettingRepository(db),
            Options.Create(new WorkspaceOptions()));
        return new WorkspaceRepository(
            db,
            dbFactory,
            workspaceService,
            new WorkspaceGitChangesNotifier(NullLogger<WorkspaceGitChangesNotifier>.Instance),
            NullLogger<WorkspaceRepository>.Instance);
    }

    public WorkspaceFileOperations FileOperations(AsyncServiceScope scope)
    {
        var db = Db(scope);
        var workspaceRepository = WorkspaceRepo(scope);
        var fileRepository = scope.ServiceProvider.GetRequiredService<WorkspaceFileRepository>();
        var fileVersionService = new WorkspaceFileVersionService(
            null!,
            workspaceRepository,
            null!,
            null!,
            db,
            null!,
            null!,
            null!,
            null!,
            NullLogger<WorkspaceFileVersionService>.Instance);
        return new WorkspaceFileOperations(
            workspaceRepository,
            fileRepository,
            null!,
            fileVersionService,
            null!,
            null!);
    }

    public async ValueTask DisposeAsync()
    {
        await _provider.DisposeAsync();
        await _connection.DisposeAsync();
    }
}
