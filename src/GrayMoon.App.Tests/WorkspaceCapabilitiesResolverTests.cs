using GrayMoon.Abstractions.Workspaces;
using GrayMoon.App.Data;
using GrayMoon.App.Models;
using GrayMoon.App.Services.Workspaces;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace GrayMoon.App.Tests;

public sealed class WorkspaceCapabilitiesResolverTests
{
    [Fact]
    public async Task Basic_without_versioning_or_ci_has_no_dotnet_version_or_ci_capabilities()
    {
        await using var context = await ResolverTestContext.CreateAsync();
        var workspaceId = await context.AddWorkspaceAsync(
            "basic",
            WorkspaceType.Basic,
            WorkspaceVersioningMode.None,
            WorkspaceCiProvider.None);

        var capabilities = await context.Resolver.GetAsync(workspaceId);

        Assert.False(capabilities.UsesRepositoryVersioning);
        Assert.False(capabilities.UsesGitVersion);
        Assert.False(capabilities.DiscoversDotNetProjects);
        Assert.False(capabilities.UsesDependencyGraph);
        Assert.False(capabilities.UsesCiIntegration);
        Assert.False(capabilities.UsesGitHubActions);
    }

    [Fact]
    public async Task Basic_with_GitVersion_versions_repositories_but_still_discovers_no_projects()
    {
        await using var context = await ResolverTestContext.CreateAsync();
        var workspaceId = await context.AddWorkspaceAsync(
            "basic-versioned",
            WorkspaceType.Basic,
            WorkspaceVersioningMode.GitVersion,
            WorkspaceCiProvider.None);

        var capabilities = await context.Resolver.GetAsync(workspaceId);

        Assert.True(capabilities.UsesRepositoryVersioning);
        Assert.True(capabilities.UsesGitVersion);
        Assert.False(capabilities.DiscoversDotNetProjects);
        Assert.False(capabilities.UsesDependencyGraph);
        Assert.False(capabilities.UsesNuGetPackages);
        Assert.False(capabilities.UsesCiIntegration);
    }

    [Fact]
    public async Task The_dotnet_triple_enables_every_dotnet_capability()
    {
        await using var context = await ResolverTestContext.CreateAsync();
        var workspaceId = await context.AddWorkspaceAsync(
            "dotnet",
            WorkspaceType.DotNetDependency,
            WorkspaceVersioningMode.GitVersion,
            WorkspaceCiProvider.GitHubActions);

        var capabilities = await context.Resolver.GetAsync(workspaceId);

        Assert.True(capabilities.UsesRepositoryVersioning);
        Assert.True(capabilities.UsesGitVersion);
        Assert.True(capabilities.DiscoversDotNetProjects);
        Assert.True(capabilities.UsesDependencyGraph);
        Assert.True(capabilities.UsesNuGetPackages);
        Assert.True(capabilities.UsesDependencyAwareUpdate);
        Assert.True(capabilities.UsesDependencyAwarePush);
        Assert.True(capabilities.UsesPackageRestore);
        Assert.True(capabilities.UsesGeneratedPackagesFromVersionFiles);
        Assert.True(capabilities.UsesCiIntegration);
        Assert.True(capabilities.UsesGitHubActions);
    }

    [Fact]
    public async Task GetAsync_throws_for_an_unknown_workspace_id()
    {
        await using var context = await ResolverTestContext.CreateAsync();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => context.Resolver.GetAsync(4242));

        Assert.Contains("4242", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetManyAsync_returns_one_entry_per_existing_id_and_omits_unknown_ids()
    {
        await using var context = await ResolverTestContext.CreateAsync();
        var basicId = await context.AddWorkspaceAsync(
            "basic",
            WorkspaceType.Basic,
            WorkspaceVersioningMode.None,
            WorkspaceCiProvider.None);
        var dotNetId = await context.AddWorkspaceAsync(
            "dotnet",
            WorkspaceType.DotNetDependency,
            WorkspaceVersioningMode.GitVersion,
            WorkspaceCiProvider.GitHubActions);

        var capabilities = await context.Resolver.GetManyAsync(new[] { basicId, dotNetId, 4242 });

        Assert.Equal(2, capabilities.Count);
        Assert.Equal(WorkspaceType.Basic, capabilities[basicId].Type);
        Assert.Equal(WorkspaceType.DotNetDependency, capabilities[dotNetId].Type);
        Assert.False(capabilities.ContainsKey(4242));
    }

    [Fact]
    public async Task GetManyAsync_returns_an_empty_result_for_an_empty_input()
    {
        await using var context = await ResolverTestContext.CreateAsync();

        var capabilities = await context.Resolver.GetManyAsync(Array.Empty<int>());

        Assert.Empty(capabilities);
    }

    [Fact]
    public async Task ToRepositoryOperationCapabilities_maps_Basic_and_the_dotnet_triple()
    {
        await using var context = await ResolverTestContext.CreateAsync();
        var basicId = await context.AddWorkspaceAsync(
            "basic",
            WorkspaceType.Basic,
            WorkspaceVersioningMode.None,
            WorkspaceCiProvider.None);
        var dotNetId = await context.AddWorkspaceAsync(
            "dotnet",
            WorkspaceType.DotNetDependency,
            WorkspaceVersioningMode.GitVersion,
            WorkspaceCiProvider.GitHubActions);

        var basic = (await context.Resolver.GetAsync(basicId)).ToRepositoryOperationCapabilities();
        Assert.False(basic.ShouldCalculateVersion);
        Assert.False(basic.ShouldDiscoverProjects);

        var dotNet = (await context.Resolver.GetAsync(dotNetId)).ToRepositoryOperationCapabilities();
        Assert.True(dotNet.ShouldCalculateVersion);
        Assert.True(dotNet.ShouldDiscoverProjects);
    }

    /// <summary>
    /// Features inherit the parent Workspace's profile; there is no per-Feature profile. Resolving by the
    /// workspaceId a Feature context belongs to must therefore produce exactly the Workspace's capabilities.
    /// </summary>
    [Fact]
    public async Task A_feature_contexts_workspace_resolves_the_same_capabilities_as_the_workspace()
    {
        await using var context = await ResolverTestContext.CreateAsync();
        var workspaceId = await context.AddWorkspaceAsync(
            "dotnet",
            WorkspaceType.DotNetDependency,
            WorkspaceVersioningMode.GitVersion,
            WorkspaceCiProvider.GitHubActions);

        var feature = new WorkspaceFeature
        {
            WorkspaceId = workspaceId,
            Name = "feat",
            LifecycleState = WorkspaceFeatureLifecycleState.Ready,
            BaseKind = WorkspaceFeatureBaseKind.CurrentWorkspace,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        context.DbContext.WorkspaceFeatures.Add(feature);
        await context.DbContext.SaveChangesAsync();

        var featureContext = new WorkspaceFeatureContext
        {
            WorkspaceId = workspaceId,
            Kind = WorkspaceFeatureContextKind.Feature,
            WorkspaceFeatureId = feature.WorkspaceFeatureId,
            CreatedAt = DateTime.UtcNow,
            IsInSync = true
        };
        context.DbContext.WorkspaceFeatureContexts.Add(featureContext);
        await context.DbContext.SaveChangesAsync();

        var fromWorkspace = await context.Resolver.GetAsync(workspaceId);
        var fromFeaturesWorkspace = await context.Resolver.GetAsync(featureContext.WorkspaceId);

        Assert.Equal(fromWorkspace, fromFeaturesWorkspace);
        Assert.True(fromFeaturesWorkspace.DiscoversDotNetProjects);
        Assert.True(fromFeaturesWorkspace.UsesGitVersion);
        Assert.True(fromFeaturesWorkspace.UsesGitHubActions);
    }

    private sealed class ResolverTestContext : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;

        public AppDbContext DbContext { get; }
        public WorkspaceCapabilitiesResolver Resolver { get; }

        private ResolverTestContext(SqliteConnection connection, AppDbContext dbContext, DbContextOptions<AppDbContext> options)
        {
            _connection = connection;
            DbContext = dbContext;
            Resolver = new WorkspaceCapabilitiesResolver(new TestDbContextFactory(options));
        }

        public static async Task<ResolverTestContext> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();

            var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
            var dbContext = new AppDbContext(options);
            await dbContext.Database.EnsureCreatedAsync();

            return new ResolverTestContext(connection, dbContext, options);
        }

        public async Task<int> AddWorkspaceAsync(
            string name,
            WorkspaceType type,
            WorkspaceVersioningMode versioningMode,
            WorkspaceCiProvider ciProvider)
        {
            var workspace = new Workspace
            {
                Name = name,
                Type = type,
                VersioningMode = versioningMode,
                CiProvider = ciProvider
            };
            DbContext.Workspaces.Add(workspace);
            await DbContext.SaveChangesAsync();
            return workspace.WorkspaceId;
        }

        public async ValueTask DisposeAsync()
        {
            await DbContext.DisposeAsync();
            await _connection.DisposeAsync();
        }

        private sealed class TestDbContextFactory(DbContextOptions<AppDbContext> options) : IDbContextFactory<AppDbContext>
        {
            public AppDbContext CreateDbContext() => new(options);
        }
    }
}
