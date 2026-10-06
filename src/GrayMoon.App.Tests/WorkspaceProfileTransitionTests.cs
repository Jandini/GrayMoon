using GrayMoon.Abstractions.Workspaces;
using GrayMoon.App.Data;
using GrayMoon.App.Models;
using GrayMoon.App.Repositories;
using GrayMoon.App.Services.Workspaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GrayMoon.App.Tests;

public sealed class WorkspaceProfileTransitionTests
{
    [Fact]
    public async Task Switching_to_Basic_clears_projects_edges_and_levels_but_leaves_git_version_and_actions()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        await ctx.UseDotNetDependencyProfileAsync();
        await SeedDerivedStateAsync(ctx);

        await SaveProfileAsync(
            ctx,
            WorkspaceType.Basic,
            WorkspaceVersioningMode.None,
            WorkspaceCiProvider.None);

        var factory = ctx.Resolve<IDbContextFactory<AppDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        Assert.Empty(await db.WorkspaceProjects.AsNoTracking().Where(p => p.WorkspaceId == ctx.WorkspaceId).ToListAsync());
        Assert.Empty(await db.ProjectDependencies.AsNoTracking().ToListAsync());

        var link = await ctx.ReadLinkAsync();
        Assert.Null(link.DependencyLevel);
        Assert.Null(link.UnmatchedDeps);
        Assert.Null(link.Dependencies);
        Assert.Null(link.RepositoryType);
        Assert.Equal("1.0.0", link.GitVersion);
        Assert.Equal("feature/x", link.BranchName);
        Assert.Equal(3, link.OutgoingCommits);

        var contextState = await db.WorkspaceRepositoryContextStates
            .AsNoTracking()
            .FirstAsync(state => state.WorkspaceRepositoryId == ctx.WorkspaceRepositoryId);
        Assert.Null(contextState.DependencyLevel);
        Assert.Null(contextState.UnmatchedDeps);
        Assert.Null(contextState.Dependencies);
        Assert.Null(contextState.RepositoryType);
        Assert.Equal("1.0.0", contextState.GitVersion);

        Assert.Single(await db.WorkspaceRepositoryActions.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Type_change_is_blocked_while_Features_exist_with_the_same_wording_as_rename()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        await ctx.UseDotNetDependencyProfileAsync();

        var factory = ctx.Resolve<IDbContextFactory<AppDbContext>>();
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.WorkspaceFeatures.Add(new WorkspaceFeature { WorkspaceId = ctx.WorkspaceId, Name = "my-feature" });
            await db.SaveChangesAsync();
        }

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => SaveProfileAsync(
            ctx,
            WorkspaceType.Basic,
            WorkspaceVersioningMode.None,
            WorkspaceCiProvider.None));

        Assert.Equal(WorkspaceProfileTransition.TypeChangeBlockedByFeaturesMessage, exception.Message);

        await using var after = await factory.CreateDbContextAsync();
        var workspace = await after.Workspaces.AsNoTracking().SingleAsync();
        Assert.Equal(WorkspaceType.DotNetDependency, workspace.Type);
    }

    [Fact]
    public async Task Versioning_or_CI_can_change_while_Features_exist()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        await ctx.UseDotNetDependencyProfileAsync();

        var factory = ctx.Resolve<IDbContextFactory<AppDbContext>>();
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.WorkspaceFeatures.Add(new WorkspaceFeature { WorkspaceId = ctx.WorkspaceId, Name = "my-feature" });
            await db.SaveChangesAsync();
        }

        await SaveProfileAsync(
            ctx,
            WorkspaceType.DotNetDependency,
            WorkspaceVersioningMode.None,
            WorkspaceCiProvider.None);

        await using var after = await factory.CreateDbContextAsync();
        var workspace = await after.Workspaces.AsNoTracking().SingleAsync();
        Assert.Equal(WorkspaceType.DotNetDependency, workspace.Type);
        Assert.Equal(WorkspaceVersioningMode.None, workspace.VersioningMode);
        Assert.Equal(WorkspaceCiProvider.None, workspace.CiProvider);
    }

    [Fact]
    public async Task Turning_versioning_off_does_not_clear_GitVersion_or_projects()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        await ctx.UseDotNetDependencyProfileAsync();
        await SeedDerivedStateAsync(ctx);

        await SaveProfileAsync(
            ctx,
            WorkspaceType.DotNetDependency,
            WorkspaceVersioningMode.None,
            WorkspaceCiProvider.GitHubActions);

        Assert.NotEmpty(await ctx.ReadProjectsAsync());
        var link = await ctx.ReadLinkAsync();
        Assert.Equal(2, link.DependencyLevel);
        Assert.Equal("1.0.0", link.GitVersion);
        Assert.Equal(ProjectType.Library, link.RepositoryType);
    }

    [Fact]
    public async Task Turning_CI_off_does_not_clear_Actions_or_git_state()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        await ctx.UseDotNetDependencyProfileAsync();
        await SeedDerivedStateAsync(ctx);

        await SaveProfileAsync(
            ctx,
            WorkspaceType.DotNetDependency,
            WorkspaceVersioningMode.GitVersion,
            WorkspaceCiProvider.None);

        var factory = ctx.Resolve<IDbContextFactory<AppDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        Assert.Single(await db.WorkspaceRepositoryActions.AsNoTracking().ToListAsync());
        Assert.NotEmpty(await ctx.ReadProjectsAsync());
        Assert.Equal("1.0.0", (await ctx.ReadLinkAsync()).GitVersion);
    }

    [Fact]
    public async Task AddAsync_persists_an_explicit_Basic_profile()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        ctx.WorkerBridge.Respond("EnsureWorkspace", new { success = true });

        await using var scope = ctx.CreateScope();
        var catalog = scope.ServiceProvider.GetRequiredService<WorkspaceRepository>();
        var created = await catalog.AddAsync(
            "docs-ws",
            [],
            WorkspaceType.Basic,
            WorkspaceVersioningMode.None,
            WorkspaceCiProvider.None);

        var factory = ctx.Resolve<IDbContextFactory<AppDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        var workspace = await db.Workspaces.AsNoTracking().SingleAsync(w => w.WorkspaceId == created.WorkspaceId);
        Assert.Equal(WorkspaceType.Basic, workspace.Type);
        Assert.Equal(WorkspaceVersioningMode.None, workspace.VersioningMode);
        Assert.Equal(WorkspaceCiProvider.None, workspace.CiProvider);
    }

    private static async Task SeedDerivedStateAsync(SyncStateTestContext ctx)
    {
        await ctx.MutateLinkAsync(link =>
        {
            link.DependencyLevel = 2;
            link.UnmatchedDeps = 1;
            link.Dependencies = 3;
            link.RepositoryType = ProjectType.Library;
        });

        var special = await ctx.GetSpecialContextIdAsync();
        var factory = ctx.Resolve<IDbContextFactory<AppDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        var producer = new WorkspaceProject
        {
            WorkspaceId = ctx.WorkspaceId,
            WorkspaceFeatureContextId = special.Value,
            RepositoryId = ctx.RepositoryId,
            ProjectName = "Acme.Api",
            PackageId = "Acme.Api",
            ProjectType = ProjectType.Library,
            ProjectFilePath = "src/Api/Acme.Api.csproj",
            TargetFramework = "net10.0",
        };
        var consumer = new WorkspaceProject
        {
            WorkspaceId = ctx.WorkspaceId,
            WorkspaceFeatureContextId = special.Value,
            RepositoryId = ctx.RepositoryId,
            ProjectName = "Acme.Web",
            ProjectType = ProjectType.Library,
            ProjectFilePath = "src/Web/Acme.Web.csproj",
            TargetFramework = "net10.0",
        };
        db.WorkspaceProjects.AddRange(producer, consumer);
        await db.SaveChangesAsync();
        db.ProjectDependencies.Add(new ProjectDependency
        {
            DependentProjectId = producer.ProjectId,
            ReferencedProjectId = consumer.ProjectId,
            Version = "1.0.0",
        });

        var contextState = await db.WorkspaceRepositoryContextStates
            .FirstAsync(state => state.WorkspaceRepositoryId == ctx.WorkspaceRepositoryId);
        contextState.DependencyLevel = 2;
        contextState.UnmatchedDeps = 1;
        contextState.Dependencies = 3;
        contextState.RepositoryType = ProjectType.Library;
        contextState.GitVersion = "1.0.0";

        db.WorkspaceRepositoryActions.Add(new WorkspaceRepositoryAction
        {
            WorkspaceRepositoryId = ctx.WorkspaceRepositoryId,
            BranchName = "feature/x",
            Status = "success",
            WorkflowsJson = "[]",
            LastCheckedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private static async Task SaveProfileAsync(
        SyncStateTestContext ctx,
        WorkspaceType type,
        WorkspaceVersioningMode versioningMode,
        WorkspaceCiProvider ciProvider)
    {
        await using var scope = ctx.CreateScope();
        var catalog = scope.ServiceProvider.GetRequiredService<WorkspaceRepository>();
        await catalog.UpdateAsync(
            ctx.WorkspaceId,
            "test-ws",
            [ctx.RepositoryId],
            @"C:\gm-test-root",
            type,
            versioningMode,
            ciProvider);
    }
}
