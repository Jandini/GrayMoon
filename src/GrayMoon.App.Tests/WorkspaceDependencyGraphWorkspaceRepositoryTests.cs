using GrayMoon.App.Models;
using GrayMoon.Application.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GrayMoon.App.Tests;

/// <summary>The Workspace-role repository has no projects (D6) and no dependency level, so it must never appear in the repository dependency graph.</summary>
public sealed class WorkspaceDependencyGraphWorkspaceRepositoryTests
{
    private const string WorkspaceRepoName = "ws-root";

    private static async Task<int> AddWorkspaceRoleRepositoryAsync(ContextScopingTestContext ctx, AsyncServiceScope scope)
    {
        var db = ctx.Db(scope);
        var connectorId = await db.Connectors.Select(c => c.ConnectorId).FirstAsync();
        var repository = new Repository { ConnectorId = connectorId, RepositoryName = WorkspaceRepoName, OrgName = "acme", Visibility = "Public", CloneUrl = "https://example/ws-root.git" };
        db.Repositories.Add(repository);
        await db.SaveChangesAsync();
        db.WorkspaceRepositories.Add(new WorkspaceRepositoryLink { WorkspaceId = ctx.WorkspaceId, RepositoryId = repository.RepositoryId, Role = WorkspaceRepositoryRole.Workspace });
        await db.SaveChangesAsync();
        return repository.RepositoryId;
    }

    [Fact]
    public async Task Graph_excludes_workspace_role_repository()
    {
        await using var ctx = await ContextScopingTestContext.CreateAsync();
        await using var scope = ctx.CreateScope();
        var workspaceRepoId = await AddWorkspaceRoleRepositoryAsync(ctx, scope);
        var repo = ctx.Repo(scope);

        var legacy = await repo.GetRepositoryDependencyGraphAsync(ctx.WorkspaceId);
        var scoped = await repo.GetRepositoryDependencyGraphAsync(ctx.WorkspaceId, (WorkspaceFeatureContextId?)new WorkspaceFeatureContextId(ctx.SpecialContextId));

        foreach (var graph in new[] { legacy, scoped })
        {
            Assert.DoesNotContain(graph.Nodes, n => n.RepositoryId == workspaceRepoId);
            Assert.Equal(2, graph.Nodes.Count);
            Assert.Single(graph.Edges);
        }
    }

    [Fact]
    public async Task Graph_without_workspace_role_repository_is_unchanged()
    {
        await using var ctx = await ContextScopingTestContext.CreateAsync();
        await using var scope = ctx.CreateScope();
        var repo = ctx.Repo(scope);

        var legacy = await repo.GetRepositoryDependencyGraphAsync(ctx.WorkspaceId);
        var scoped = await repo.GetRepositoryDependencyGraphAsync(ctx.WorkspaceId, (WorkspaceFeatureContextId?)new WorkspaceFeatureContextId(ctx.SpecialContextId));

        foreach (var graph in new[] { legacy, scoped })
        {
            Assert.Equal(new[] { ctx.RepoAId, ctx.RepoBId }.Order(), graph.Nodes.Select(n => n.RepositoryId).Order());
            var edge = Assert.Single(graph.Edges);
            Assert.Equal(ctx.RepoAId, edge.DependentRepositoryId);
            Assert.Equal(ctx.RepoBId, edge.ReferencedRepositoryId);
        }
    }

    [Fact]
    public async Task Graph_excludes_workspace_role_repository_in_feature_context()
    {
        await using var ctx = await ContextScopingTestContext.CreateAsync();
        await using var scope = ctx.CreateScope();
        var workspaceRepoId = await AddWorkspaceRoleRepositoryAsync(ctx, scope);
        var repo = ctx.Repo(scope);

        var featureGraph = await repo.GetRepositoryDependencyGraphAsync(ctx.WorkspaceId, (WorkspaceFeatureContextId?)new WorkspaceFeatureContextId(ctx.FeatureContextId));

        Assert.DoesNotContain(featureGraph.Nodes, n => n.RepositoryId == workspaceRepoId);
        Assert.Equal(2, featureGraph.Nodes.Count);
        Assert.Single(featureGraph.Edges);
    }

    [Fact]
    public async Task Recompute_gives_workspace_role_repository_no_dependency_level()
    {
        await using var ctx = await ContextScopingTestContext.CreateAsync();
        await using var scope = ctx.CreateScope();
        var workspaceRepoId = await AddWorkspaceRoleRepositoryAsync(ctx, scope);
        var repo = ctx.Repo(scope);

        await repo.RecomputeAndPersistRepositoryDependencyStatsAsync(ctx.WorkspaceId, ctx.SpecialContextId);

        var links = await ctx.Db(scope).WorkspaceRepositories.AsNoTracking().Where(l => l.WorkspaceId == ctx.WorkspaceId).ToListAsync();
        Assert.Null(links.Single(l => l.RepositoryId == workspaceRepoId).DependencyLevel);
        Assert.Equal(1, links.Single(l => l.RepositoryId == ctx.RepoBId).DependencyLevel);
        Assert.Equal(2, links.Single(l => l.RepositoryId == ctx.RepoAId).DependencyLevel);
    }
}