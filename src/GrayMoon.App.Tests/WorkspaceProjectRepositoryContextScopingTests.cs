using GrayMoon.App.Data;
using GrayMoon.App.Models;
using GrayMoon.App.Repositories;
using GrayMoon.Application.Features;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace GrayMoon.App.Tests;

/// <summary>
/// B3: covers context scoping (feature-context-scoping.mdc) for WorkspaceProjectRepository's project/dependency
/// query methods used by Restore, the Dependencies page, and grid tooltips. Each test seeds two sibling contexts
/// (the special Workspace plus one Feature) with deliberately different projects, dependencies and versions, then
/// asserts a scoped read never mixes the two.
/// </summary>
public sealed class WorkspaceProjectRepositoryContextScopingTests
{
    [Fact]
    public async Task GetByWorkspaceIdAsync_context_overload_returns_only_that_context_rows()
    {
        await using var ctx = await ContextScopingTestContext.CreateAsync();
        await using var scope = ctx.CreateScope();
        var repo = ctx.Repo(scope);

        var workspaceProjects = await repo.GetByWorkspaceIdAsync(ctx.WorkspaceId, (WorkspaceFeatureContextId?)new WorkspaceFeatureContextId(ctx.SpecialContextId));
        Assert.Equal(new[] { ctx.WorkspaceProjectAId, ctx.WorkspaceProjectBId }.OrderBy(x => x), workspaceProjects.Select(p => p.ProjectId).OrderBy(x => x));

        var featureProjects = await repo.GetByWorkspaceIdAsync(ctx.WorkspaceId, (WorkspaceFeatureContextId?)new WorkspaceFeatureContextId(ctx.FeatureContextId));
        Assert.Equal(new[] { ctx.FeatureProjectAId, ctx.FeatureProjectBId }.OrderBy(x => x), featureProjects.Select(p => p.ProjectId).OrderBy(x => x));
    }

    [Fact]
    public async Task GetByWorkspaceIdAsync_special_workspace_also_includes_legacy_null_context_rows()
    {
        await using var ctx = await ContextScopingTestContext.CreateAsync();
        await using var scope = ctx.CreateScope();
        var db = ctx.Db(scope);
        var repo = ctx.Repo(scope);

        // Simulate an upgraded database that has a legacy project row never resynced since the
        // WorkspaceFeatureContextId column was added (null, not the special context id).
        db.WorkspaceProjects.Add(new WorkspaceProject
        {
            WorkspaceId = ctx.WorkspaceId,
            WorkspaceFeatureContextId = null,
            RepositoryId = ctx.RepoAId,
            ProjectName = "Legacy.Unsynced",
            ProjectType = ProjectType.Library,
            ProjectFilePath = "src/A/Legacy.csproj",
            TargetFramework = "net10.0",
        });
        await db.SaveChangesAsync();

        var workspaceProjects = await repo.GetByWorkspaceIdAsync(ctx.WorkspaceId, (WorkspaceFeatureContextId?)new WorkspaceFeatureContextId(ctx.SpecialContextId));
        Assert.Contains(workspaceProjects, p => p.ProjectName == "Legacy.Unsynced");

        var featureProjects = await repo.GetByWorkspaceIdAsync(ctx.WorkspaceId, (WorkspaceFeatureContextId?)new WorkspaceFeatureContextId(ctx.FeatureContextId));
        Assert.DoesNotContain(featureProjects, p => p.ProjectName == "Legacy.Unsynced");
    }

    [Fact]
    public async Task GetDependencyEdgesAsync_context_overload_does_not_mix_contexts()
    {
        await using var ctx = await ContextScopingTestContext.CreateAsync();
        await using var scope = ctx.CreateScope();
        var repo = ctx.Repo(scope);

        var workspaceEdges = await repo.GetDependencyEdgesAsync(ctx.WorkspaceId, (WorkspaceFeatureContextId?)new WorkspaceFeatureContextId(ctx.SpecialContextId));
        Assert.Equal(new[] { (ctx.WorkspaceProjectAId, ctx.WorkspaceProjectBId) }, workspaceEdges);

        var featureEdges = await repo.GetDependencyEdgesAsync(ctx.WorkspaceId, (WorkspaceFeatureContextId?)new WorkspaceFeatureContextId(ctx.FeatureContextId));
        Assert.Equal(new[] { (ctx.FeatureProjectAId, ctx.FeatureProjectBId) }, featureEdges);
    }

    [Fact]
    public async Task GetPackageDependencyLinesByRepoAsync_context_overload_does_not_mix_contexts()
    {
        await using var ctx = await ContextScopingTestContext.CreateAsync();
        await using var scope = ctx.CreateScope();
        var repo = ctx.Repo(scope);

        var workspaceLines = await repo.GetPackageDependencyLinesByRepoAsync(ctx.WorkspaceId, (WorkspaceFeatureContextId?)new WorkspaceFeatureContextId(ctx.SpecialContextId));
        Assert.True(workspaceLines.TryGetValue(ctx.RepoAId, out var wsLines));
        Assert.Equal(new[] { ("Repo.B.Package", "1.0.0") }, wsLines);

        var featureLines = await repo.GetPackageDependencyLinesByRepoAsync(ctx.WorkspaceId, (WorkspaceFeatureContextId?)new WorkspaceFeatureContextId(ctx.FeatureContextId));
        Assert.True(featureLines.TryGetValue(ctx.RepoAId, out var featLines));
        Assert.Equal(new[] { ("Repo.B.Package", "2.0.0-feature") }, featLines);
    }

    [Fact]
    public async Task GetPackageDependencyLinesForRepoAsync_context_overload_does_not_mix_contexts()
    {
        await using var ctx = await ContextScopingTestContext.CreateAsync();
        await using var scope = ctx.CreateScope();
        var repo = ctx.Repo(scope);

        var workspaceLines = await repo.GetPackageDependencyLinesForRepoAsync(ctx.WorkspaceId, ctx.RepoAId, (WorkspaceFeatureContextId?)new WorkspaceFeatureContextId(ctx.SpecialContextId));
        Assert.Equal(new[] { ("Repo.B.Package", "1.0.0") }, workspaceLines);

        var featureLines = await repo.GetPackageDependencyLinesForRepoAsync(ctx.WorkspaceId, ctx.RepoAId, (WorkspaceFeatureContextId?)new WorkspaceFeatureContextId(ctx.FeatureContextId));
        Assert.Equal(new[] { ("Repo.B.Package", "2.0.0-feature") }, featureLines);
    }

    [Fact]
    public async Task GetMismatchedDependencyLinesForRepoAsync_feature_uses_context_state_version_not_shared_link()
    {
        await using var ctx = await ContextScopingTestContext.CreateAsync();
        await using var scope = ctx.CreateScope();
        var db = ctx.Db(scope);
        var repo = ctx.Repo(scope);

        // Shared link (Workspace) version for Repo B is "1.0.0" (matches the Workspace-context dependency's
        // recorded version, so no mismatch there). The Feature's own context-state version for Repo B is
        // deliberately different ("9.9.9-context") from both the shared link and the Feature's own recorded
        // dependency version ("2.0.0-feature"), so only reading the context state - not the shared link -
        // can produce the mismatch asserted below.
        db.WorkspaceRepositoryContextStates.Add(new WorkspaceRepositoryContextState
        {
            WorkspaceFeatureContextId = ctx.FeatureContextId,
            WorkspaceRepositoryId = ctx.WorkspaceRepositoryBId,
            GitVersion = "9.9.9-context",
        });
        await db.SaveChangesAsync();

        var workspaceMismatches = await repo.GetMismatchedDependencyLinesForRepoAsync(ctx.WorkspaceId, ctx.RepoAId, (WorkspaceFeatureContextId?)new WorkspaceFeatureContextId(ctx.SpecialContextId));
        Assert.Empty(workspaceMismatches);

        var featureMismatches = await repo.GetMismatchedDependencyLinesForRepoAsync(ctx.WorkspaceId, ctx.RepoAId, (WorkspaceFeatureContextId?)new WorkspaceFeatureContextId(ctx.FeatureContextId));
        var mismatch = Assert.Single(featureMismatches);
        Assert.Equal("Repo.B.Package", mismatch.PackageId);
        Assert.Equal("2.0.0-feature", mismatch.CurrentVersion);
        Assert.Equal("9.9.9-context", mismatch.NewVersion);
    }

    [Fact]
    public async Task GetMismatchedDependencyLinesForRepoAsync_feature_with_no_context_state_reports_no_mismatch()
    {
        // Rule 2 of feature-context-scoping.mdc: a Feature with no context-state row yet has an unknown
        // version, never the Workspace's own value - so no (possibly false) mismatch is reported.
        await using var ctx = await ContextScopingTestContext.CreateAsync();
        await using var scope = ctx.CreateScope();
        var repo = ctx.Repo(scope);

        var featureMismatches = await repo.GetMismatchedDependencyLinesForRepoAsync(ctx.WorkspaceId, ctx.RepoAId, (WorkspaceFeatureContextId?)new WorkspaceFeatureContextId(ctx.FeatureContextId));
        Assert.Empty(featureMismatches);
    }

    [Fact]
    public async Task GetRepositoryDependencyGraphAsync_context_overload_does_not_mix_contexts()
    {
        await using var ctx = await ContextScopingTestContext.CreateAsync();
        await using var scope = ctx.CreateScope();
        var repo = ctx.Repo(scope);

        var workspaceGraph = await repo.GetRepositoryDependencyGraphAsync(ctx.WorkspaceId, (WorkspaceFeatureContextId?)new WorkspaceFeatureContextId(ctx.SpecialContextId));
        Assert.Contains(workspaceGraph.Edges, e => e.DependentRepositoryId == ctx.RepoAId && e.ReferencedRepositoryId == ctx.RepoBId);

        var featureGraph = await repo.GetRepositoryDependencyGraphAsync(ctx.WorkspaceId, (WorkspaceFeatureContextId?)new WorkspaceFeatureContextId(ctx.FeatureContextId));
        Assert.Contains(featureGraph.Edges, e => e.DependentRepositoryId == ctx.RepoAId && e.ReferencedRepositoryId == ctx.RepoBId);

        // Both graphs have the same single edge; the point of this test is that computing either one only
        // reads its own context's projects/edges (proven by the dependency-edges and project tests above),
        // not that the graphs differ in shape for this particular fixture.
        Assert.Single(workspaceGraph.Edges);
        Assert.Single(featureGraph.Edges);
    }

    // --- "Pin the Workspace first" (rule A4.0): characterization tests recording today's result for the
    // special Workspace on a database with no Features, kept green after the change. ---

    [Fact]
    public async Task Characterization_GetByWorkspaceIdAsync_workspace_only_database_unaffected()
    {
        await using var ctx = await WorkspaceOnlyTestContext.CreateAsync();
        await using var scope = ctx.CreateScope();
        var repo = ctx.Repo(scope);

        var legacy = await repo.GetByWorkspaceIdAsync(ctx.WorkspaceId);
        var scoped = await repo.GetByWorkspaceIdAsync(ctx.WorkspaceId, (WorkspaceFeatureContextId?)new WorkspaceFeatureContextId(ctx.SpecialContextId));

        Assert.Equal(
            legacy.Select(p => p.ProjectId).OrderBy(x => x),
            scoped.Select(p => p.ProjectId).OrderBy(x => x));
        Assert.Equal(2, scoped.Count);
    }

    [Fact]
    public async Task Characterization_GetDependencyEdgesAsync_workspace_only_database_unaffected()
    {
        await using var ctx = await WorkspaceOnlyTestContext.CreateAsync();
        await using var scope = ctx.CreateScope();
        var repo = ctx.Repo(scope);

        var legacy = await repo.GetDependencyEdgesAsync(ctx.WorkspaceId);
        var scoped = await repo.GetDependencyEdgesAsync(ctx.WorkspaceId, (WorkspaceFeatureContextId?)new WorkspaceFeatureContextId(ctx.SpecialContextId));

        Assert.Equal(legacy.OrderBy(e => e.DependentProjectId), scoped.OrderBy(e => e.DependentProjectId));
        Assert.Single(scoped);
    }

    [Fact]
    public async Task Characterization_GetPackageDependencyLinesByRepoAsync_workspace_only_database_unaffected()
    {
        await using var ctx = await WorkspaceOnlyTestContext.CreateAsync();
        await using var scope = ctx.CreateScope();
        var repo = ctx.Repo(scope);

        var legacy = await repo.GetPackageDependencyLinesByRepoAsync(ctx.WorkspaceId);
        var scoped = await repo.GetPackageDependencyLinesByRepoAsync(ctx.WorkspaceId, (WorkspaceFeatureContextId?)new WorkspaceFeatureContextId(ctx.SpecialContextId));

        Assert.Equal(legacy.Keys.OrderBy(x => x), scoped.Keys.OrderBy(x => x));
        foreach (var key in legacy.Keys)
            Assert.Equal(legacy[key], scoped[key]);
    }

    [Fact]
    public async Task Characterization_GetPackageDependencyLinesForRepoAsync_workspace_only_database_unaffected()
    {
        await using var ctx = await WorkspaceOnlyTestContext.CreateAsync();
        await using var scope = ctx.CreateScope();
        var repo = ctx.Repo(scope);

        var legacy = await repo.GetPackageDependencyLinesForRepoAsync(ctx.WorkspaceId, ctx.RepoAId);
        var scoped = await repo.GetPackageDependencyLinesForRepoAsync(ctx.WorkspaceId, ctx.RepoAId, (WorkspaceFeatureContextId?)new WorkspaceFeatureContextId(ctx.SpecialContextId));

        Assert.Equal(legacy, scoped);
        Assert.Single(scoped);
    }

    [Fact]
    public async Task Characterization_GetMismatchedDependencyLinesForRepoAsync_workspace_only_database_unaffected()
    {
        await using var ctx = await WorkspaceOnlyTestContext.CreateAsync();
        await using var scope = ctx.CreateScope();
        var repo = ctx.Repo(scope);

        var legacy = await repo.GetMismatchedDependencyLinesForRepoAsync(ctx.WorkspaceId, ctx.RepoAId);
        var scoped = await repo.GetMismatchedDependencyLinesForRepoAsync(ctx.WorkspaceId, ctx.RepoAId, (WorkspaceFeatureContextId?)new WorkspaceFeatureContextId(ctx.SpecialContextId));

        Assert.Equal(legacy, scoped);
        // The fixture's recorded dependency version matches the shared link's GitVersion, so there is no
        // mismatch today, and must still be none after scoping.
        Assert.Empty(scoped);
    }

    [Fact]
    public async Task Characterization_GetRepositoryDependencyGraphAsync_workspace_only_database_unaffected()
    {
        await using var ctx = await WorkspaceOnlyTestContext.CreateAsync();
        await using var scope = ctx.CreateScope();
        var repo = ctx.Repo(scope);

        var legacy = await repo.GetRepositoryDependencyGraphAsync(ctx.WorkspaceId);
        var scoped = await repo.GetRepositoryDependencyGraphAsync(ctx.WorkspaceId, (WorkspaceFeatureContextId?)new WorkspaceFeatureContextId(ctx.SpecialContextId));

        Assert.Equal(
            legacy.Nodes.Select(n => n.RepositoryId).OrderBy(x => x),
            scoped.Nodes.Select(n => n.RepositoryId).OrderBy(x => x));
        Assert.Equal(
            legacy.Edges.Select(e => (e.DependentRepositoryId, e.ReferencedRepositoryId)),
            scoped.Edges.Select(e => (e.DependentRepositoryId, e.ReferencedRepositoryId)));
    }

    [Fact]
    public async Task Characterization_GetDependencyGraphAsync_workspace_only_database_unaffected()
    {
        await using var ctx = await WorkspaceOnlyTestContext.CreateAsync();
        await using var scope = ctx.CreateScope();
        var repo = ctx.Repo(scope);

        var legacy = await repo.GetDependencyGraphAsync(ctx.WorkspaceId);
        var scoped = await repo.GetDependencyGraphAsync(ctx.WorkspaceId, (WorkspaceFeatureContextId?)new WorkspaceFeatureContextId(ctx.SpecialContextId));

        Assert.Equal(
            legacy.Nodes.Select(n => n.ProjectId).OrderBy(x => x),
            scoped.Nodes.Select(n => n.ProjectId).OrderBy(x => x));
        Assert.Equal(
            legacy.Edges.Select(e => (e.DependentProjectId, e.ReferencedProjectId)),
            scoped.Edges.Select(e => (e.DependentProjectId, e.ReferencedProjectId)));
    }
}

/// <summary>
/// In-memory SQLite DI context seeded with two repositories (A depends on B) duplicated across two sibling
/// contexts of the same workspace: the special Workspace context and one Feature context, each with its own
/// WorkspaceProject rows, ProjectDependency edge and recorded dependency version, so a scoped read that
/// accidentally mixed contexts would be caught immediately.
/// </summary>
public sealed class ContextScopingTestContext : IAsyncDisposable
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
    public int WorkspaceProjectAId { get; private set; }
    public int WorkspaceProjectBId { get; private set; }
    public int FeatureProjectAId { get; private set; }
    public int FeatureProjectBId { get; private set; }

    private ContextScopingTestContext(SqliteConnection connection, ServiceProvider provider)
    {
        _connection = connection;
        _provider = provider;
    }

    public static async Task<ContextScopingTestContext> CreateAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddDbContext<AppDbContext>(o => o.UseSqlite(connection), ServiceLifetime.Scoped);
        services.AddScoped<WorkspaceFileVersionConfigRepository>();
        services.AddScoped<WorkspaceRepositoryCustomDependencyRepository>();
        services.AddScoped<WorkspaceProjectRepository>();

        var provider = services.BuildServiceProvider();
        var ctx = new ContextScopingTestContext(connection, provider);
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

        var workspace = new Workspace { Name = "context-scoping-ws" };
        db.Workspaces.Add(workspace);
        await db.SaveChangesAsync();

        var linkA = new WorkspaceRepositoryLink { WorkspaceId = workspace.WorkspaceId, RepositoryId = repoA.RepositoryId, GitVersion = "1.0.0" };
        var linkB = new WorkspaceRepositoryLink { WorkspaceId = workspace.WorkspaceId, RepositoryId = repoB.RepositoryId, GitVersion = "1.0.0" };
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
            Name = "feature-a",
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

        // Workspace-context projects: A depends on B's package at version 1.0.0.
        var wsProjectA = new WorkspaceProject
        {
            WorkspaceId = workspace.WorkspaceId,
            WorkspaceFeatureContextId = specialContext.WorkspaceFeatureContextId,
            RepositoryId = repoA.RepositoryId,
            ProjectName = "Repo.A",
            ProjectType = ProjectType.Service,
            ProjectFilePath = "src/A/Repo.A.csproj",
            TargetFramework = "net10.0",
        };
        var wsProjectB = new WorkspaceProject
        {
            WorkspaceId = workspace.WorkspaceId,
            WorkspaceFeatureContextId = specialContext.WorkspaceFeatureContextId,
            RepositoryId = repoB.RepositoryId,
            ProjectName = "Repo.B",
            ProjectType = ProjectType.Package,
            ProjectFilePath = "src/B/Repo.B.csproj",
            TargetFramework = "net10.0",
            PackageId = "Repo.B.Package",
        };
        db.WorkspaceProjects.AddRange(wsProjectA, wsProjectB);
        await db.SaveChangesAsync();
        db.ProjectDependencies.Add(new ProjectDependency { DependentProjectId = wsProjectA.ProjectId, ReferencedProjectId = wsProjectB.ProjectId, Version = "1.0.0" });
        await db.SaveChangesAsync();

        // Feature-context projects: same shape, deliberately different recorded version so a mixed-up read
        // is caught.
        var featProjectA = new WorkspaceProject
        {
            WorkspaceId = workspace.WorkspaceId,
            WorkspaceFeatureContextId = featureContext.WorkspaceFeatureContextId,
            RepositoryId = repoA.RepositoryId,
            ProjectName = "Repo.A",
            ProjectType = ProjectType.Service,
            ProjectFilePath = "src/A/Repo.A.csproj",
            TargetFramework = "net10.0",
        };
        var featProjectB = new WorkspaceProject
        {
            WorkspaceId = workspace.WorkspaceId,
            WorkspaceFeatureContextId = featureContext.WorkspaceFeatureContextId,
            RepositoryId = repoB.RepositoryId,
            ProjectName = "Repo.B",
            ProjectType = ProjectType.Package,
            ProjectFilePath = "src/B/Repo.B.csproj",
            TargetFramework = "net10.0",
            PackageId = "Repo.B.Package",
        };
        db.WorkspaceProjects.AddRange(featProjectA, featProjectB);
        await db.SaveChangesAsync();
        db.ProjectDependencies.Add(new ProjectDependency { DependentProjectId = featProjectA.ProjectId, ReferencedProjectId = featProjectB.ProjectId, Version = "2.0.0-feature" });
        await db.SaveChangesAsync();

        WorkspaceId = workspace.WorkspaceId;
        RepoAId = repoA.RepositoryId;
        RepoBId = repoB.RepositoryId;
        WorkspaceRepositoryAId = linkA.WorkspaceRepositoryId;
        WorkspaceRepositoryBId = linkB.WorkspaceRepositoryId;
        SpecialContextId = specialContext.WorkspaceFeatureContextId;
        FeatureContextId = featureContext.WorkspaceFeatureContextId;
        WorkspaceProjectAId = wsProjectA.ProjectId;
        WorkspaceProjectBId = wsProjectB.ProjectId;
        FeatureProjectAId = featProjectA.ProjectId;
        FeatureProjectBId = featProjectB.ProjectId;
    }

    public AsyncServiceScope CreateScope() => _provider.CreateAsyncScope();

    public WorkspaceProjectRepository Repo(AsyncServiceScope scope) => scope.ServiceProvider.GetRequiredService<WorkspaceProjectRepository>();

    public AppDbContext Db(AsyncServiceScope scope) => scope.ServiceProvider.GetRequiredService<AppDbContext>();

    public async ValueTask DisposeAsync()
    {
        await _provider.DisposeAsync();
        await _connection.DisposeAsync();
    }
}

/// <summary>
/// In-memory SQLite DI context with no Features at all - only the special Workspace context - for the
/// "pin the Workspace first" characterization tests (rule A4.0).
/// </summary>
public sealed class WorkspaceOnlyTestContext : IAsyncDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _provider;

    public int WorkspaceId { get; private set; }
    public int RepoAId { get; private set; }
    public int SpecialContextId { get; private set; }

    private WorkspaceOnlyTestContext(SqliteConnection connection, ServiceProvider provider)
    {
        _connection = connection;
        _provider = provider;
    }

    public static async Task<WorkspaceOnlyTestContext> CreateAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddDbContext<AppDbContext>(o => o.UseSqlite(connection), ServiceLifetime.Scoped);
        services.AddScoped<WorkspaceFileVersionConfigRepository>();
        services.AddScoped<WorkspaceRepositoryCustomDependencyRepository>();
        services.AddScoped<WorkspaceProjectRepository>();

        var provider = services.BuildServiceProvider();
        var ctx = new WorkspaceOnlyTestContext(connection, provider);
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

        var workspace = new Workspace { Name = "workspace-only-ws" };
        db.Workspaces.Add(workspace);
        await db.SaveChangesAsync();

        var linkA = new WorkspaceRepositoryLink { WorkspaceId = workspace.WorkspaceId, RepositoryId = repoA.RepositoryId, GitVersion = "1.0.0" };
        var linkB = new WorkspaceRepositoryLink { WorkspaceId = workspace.WorkspaceId, RepositoryId = repoB.RepositoryId, GitVersion = "1.0.0" };
        db.WorkspaceRepositories.AddRange(linkA, linkB);
        await db.SaveChangesAsync();

        var specialContext = new WorkspaceFeatureContext
        {
            WorkspaceId = workspace.WorkspaceId,
            Kind = WorkspaceFeatureContextKind.Workspace,
            CreatedAt = DateTime.UtcNow,
            IsInSync = true,
        };
        db.WorkspaceFeatureContexts.Add(specialContext);
        await db.SaveChangesAsync();

        var projectA = new WorkspaceProject
        {
            WorkspaceId = workspace.WorkspaceId,
            WorkspaceFeatureContextId = specialContext.WorkspaceFeatureContextId,
            RepositoryId = repoA.RepositoryId,
            ProjectName = "Repo.A",
            ProjectType = ProjectType.Service,
            ProjectFilePath = "src/A/Repo.A.csproj",
            TargetFramework = "net10.0",
        };
        var projectB = new WorkspaceProject
        {
            WorkspaceId = workspace.WorkspaceId,
            WorkspaceFeatureContextId = specialContext.WorkspaceFeatureContextId,
            RepositoryId = repoB.RepositoryId,
            ProjectName = "Repo.B",
            ProjectType = ProjectType.Package,
            ProjectFilePath = "src/B/Repo.B.csproj",
            TargetFramework = "net10.0",
            PackageId = "Repo.B.Package",
        };
        db.WorkspaceProjects.AddRange(projectA, projectB);
        await db.SaveChangesAsync();
        db.ProjectDependencies.Add(new ProjectDependency { DependentProjectId = projectA.ProjectId, ReferencedProjectId = projectB.ProjectId, Version = "1.0.0" });
        await db.SaveChangesAsync();

        WorkspaceId = workspace.WorkspaceId;
        RepoAId = repoA.RepositoryId;
        SpecialContextId = specialContext.WorkspaceFeatureContextId;
    }

    public AsyncServiceScope CreateScope() => _provider.CreateAsyncScope();

    public WorkspaceProjectRepository Repo(AsyncServiceScope scope) => scope.ServiceProvider.GetRequiredService<WorkspaceProjectRepository>();

    public AppDbContext Db(AsyncServiceScope scope) => scope.ServiceProvider.GetRequiredService<AppDbContext>();

    public async ValueTask DisposeAsync()
    {
        await _provider.DisposeAsync();
        await _connection.DisposeAsync();
    }
}
