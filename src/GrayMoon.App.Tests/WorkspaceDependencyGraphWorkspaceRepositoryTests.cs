using GrayMoon.App.Models;
using GrayMoon.Application.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GrayMoon.App.Tests;

/// <summary>
/// The Workspace-role repository takes part in the repository dependency graph: its own projects, version files and
/// custom dependencies make it depend on source repositories. It is always the top level, whatever level its own edges
/// would give it, and nothing may depend on it (an edge into it is dropped, never a cycle).
/// Fixture: repo-a depends on repo-b (levels 2 and 1).
/// </summary>
public sealed class WorkspaceDependencyGraphWorkspaceRepositoryTests
{
    private const string WorkspaceRepoName = "ws-root";

    private static async Task<(int RepositoryId, int WorkspaceRepositoryId)> AddWorkspaceRoleRepositoryAsync(ContextScopingTestContext ctx, AsyncServiceScope scope)
    {
        var db = ctx.Db(scope);
        var connectorId = await db.Connectors.Select(c => c.ConnectorId).FirstAsync();
        var repository = new Repository { ConnectorId = connectorId, RepositoryName = WorkspaceRepoName, OrgName = "acme", Visibility = "Public", CloneUrl = "https://example/ws-root.git" };
        db.Repositories.Add(repository);
        await db.SaveChangesAsync();
        var link = new WorkspaceRepositoryLink { WorkspaceId = ctx.WorkspaceId, RepositoryId = repository.RepositoryId, Role = WorkspaceRepositoryRole.Workspace, GitVersion = "3.0.0" };
        db.WorkspaceRepositories.Add(link);
        await db.SaveChangesAsync();
        return (repository.RepositoryId, link.WorkspaceRepositoryId);
    }

    private static async Task AddVersionFileAsync(ContextScopingTestContext ctx, AsyncServiceScope scope, int repositoryId, string pattern)
    {
        var db = ctx.Db(scope);
        var file = new WorkspaceFile
        {
            WorkspaceId = ctx.WorkspaceId,
            RepositoryId = repositoryId,
            FileName = "versions.env",
            FilePath = "versions.env",
        };
        db.WorkspaceFiles.Add(file);
        await db.SaveChangesAsync();
        db.WorkspaceFileVersionConfigs.Add(new WorkspaceFileVersionConfig { FileId = file.FileId, VersionPattern = pattern });
        await db.SaveChangesAsync();
    }

    /// <summary>A Workspace-context project in <paramref name="repositoryId"/> that references repo-b's package at <paramref name="version"/>.</summary>
    private static async Task<int> AddProjectReferencingRepoBAsync(ContextScopingTestContext ctx, AsyncServiceScope scope, int repositoryId, string version)
    {
        var db = ctx.Db(scope);
        var project = new WorkspaceProject
        {
            WorkspaceId = ctx.WorkspaceId,
            WorkspaceFeatureContextId = ctx.SpecialContextId,
            RepositoryId = repositoryId,
            ProjectName = "Ws.Tools",
            ProjectType = ProjectType.Executable,
            ProjectFilePath = "tools/Ws.Tools.csproj",
            TargetFramework = "net10.0",
        };
        db.WorkspaceProjects.Add(project);
        await db.SaveChangesAsync();
        db.ProjectDependencies.Add(new ProjectDependency { DependentProjectId = project.ProjectId, ReferencedProjectId = ctx.WorkspaceProjectBId, Version = version });
        await db.SaveChangesAsync();
        return project.ProjectId;
    }

    private static async Task<WorkspaceRepositoryLink> LinkAsync(ContextScopingTestContext ctx, AsyncServiceScope scope, int repositoryId)
        => await ctx.Db(scope).WorkspaceRepositories.AsNoTracking().SingleAsync(l => l.WorkspaceId == ctx.WorkspaceId && l.RepositoryId == repositoryId);

    [Fact]
    public async Task Graph_includes_workspace_role_repository_as_a_node()
    {
        await using var ctx = await ContextScopingTestContext.CreateAsync();
        await using var scope = ctx.CreateScope();
        var (workspaceRepoId, _) = await AddWorkspaceRoleRepositoryAsync(ctx, scope);
        var repo = ctx.Repo(scope);

        var legacy = await repo.GetRepositoryDependencyGraphAsync(ctx.WorkspaceId);
        var scoped = await repo.GetRepositoryDependencyGraphAsync(ctx.WorkspaceId, (WorkspaceFeatureContextId?)new WorkspaceFeatureContextId(ctx.SpecialContextId));
        var feature = await repo.GetRepositoryDependencyGraphAsync(ctx.WorkspaceId, (WorkspaceFeatureContextId?)new WorkspaceFeatureContextId(ctx.FeatureContextId));

        foreach (var graph in new[] { legacy, scoped, feature })
        {
            Assert.Contains(graph.Nodes, n => n.RepositoryId == workspaceRepoId);
            Assert.Equal(3, graph.Nodes.Count);
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
    public async Task Recompute_puts_workspace_role_repository_on_the_top_level_even_without_edges()
    {
        await using var ctx = await ContextScopingTestContext.CreateAsync();
        await using var scope = ctx.CreateScope();
        var (workspaceRepoId, _) = await AddWorkspaceRoleRepositoryAsync(ctx, scope);
        var repo = ctx.Repo(scope);

        await repo.RecomputeAndPersistRepositoryDependencyStatsAsync(ctx.WorkspaceId, ctx.SpecialContextId);

        Assert.Equal(3, (await LinkAsync(ctx, scope, workspaceRepoId)).DependencyLevel);
        Assert.Equal(0, (await LinkAsync(ctx, scope, workspaceRepoId)).Dependencies);
        Assert.Equal(1, (await LinkAsync(ctx, scope, ctx.RepoBId)).DependencyLevel);
        Assert.Equal(2, (await LinkAsync(ctx, scope, ctx.RepoAId)).DependencyLevel);
    }

    [Fact]
    public async Task Version_file_in_workspace_role_repository_adds_its_edges_and_keeps_it_on_top()
    {
        await using var ctx = await ContextScopingTestContext.CreateAsync();
        await using var scope = ctx.CreateScope();
        var (workspaceRepoId, _) = await AddWorkspaceRoleRepositoryAsync(ctx, scope);
        // Its own edge alone would place it on level 2, beside repo-a.
        await AddVersionFileAsync(ctx, scope, workspaceRepoId, "REPO_B={repo-b}");
        var repo = ctx.Repo(scope);

        await repo.RecomputeAndPersistRepositoryDependencyStatsAsync(ctx.WorkspaceId, ctx.SpecialContextId);

        var graph = await repo.GetRepositoryDependencyGraphAsync(ctx.WorkspaceId, (WorkspaceFeatureContextId?)new WorkspaceFeatureContextId(ctx.SpecialContextId));
        Assert.Contains(graph.Edges, e => e.DependentRepositoryId == workspaceRepoId && e.ReferencedRepositoryId == ctx.RepoBId);
        var implicitBySource = await repo.GetImplicitReferencedRepoIdsBySourceAsync(ctx.WorkspaceId, workspaceRepoId);
        Assert.Equal(new[] { ctx.RepoBId }, implicitBySource.FromFile);

        var link = await LinkAsync(ctx, scope, workspaceRepoId);
        Assert.Equal(3, link.DependencyLevel);
        Assert.Equal(1, link.Dependencies);
    }

    [Fact]
    public async Task Projects_of_workspace_role_repository_add_edges_unmatched_counts_and_update_payload()
    {
        await using var ctx = await ContextScopingTestContext.CreateAsync();
        await using var scope = ctx.CreateScope();
        var (workspaceRepoId, _) = await AddWorkspaceRoleRepositoryAsync(ctx, scope);
        // repo-b is checked out at 1.0.0; the Workspace repository still references 0.9.0.
        await AddProjectReferencingRepoBAsync(ctx, scope, workspaceRepoId, "0.9.0");
        var repo = ctx.Repo(scope);

        await repo.RecomputeAndPersistRepositoryDependencyStatsAsync(ctx.WorkspaceId, ctx.SpecialContextId);

        var link = await LinkAsync(ctx, scope, workspaceRepoId);
        Assert.Equal(3, link.DependencyLevel);
        Assert.Equal(1, link.Dependencies);
        Assert.Equal(1, link.UnmatchedDeps);

        // Recompute created the special context's state rows; a real sync fills their GitVersion, which the payload reads.
        var db = ctx.Db(scope);
        foreach (var state in await db.WorkspaceRepositoryContextStates.Include(s => s.WorkspaceRepository).Where(s => s.WorkspaceFeatureContextId == ctx.SpecialContextId).ToListAsync())
            state.GitVersion = state.WorkspaceRepository!.GitVersion;
        await db.SaveChangesAsync();

        var payload = await repo.GetSyncDependenciesPayloadAsync(ctx.WorkspaceId, ctx.SpecialContextId);
        var workspaceUpdate = Assert.Single(payload, p => p.RepoId == workspaceRepoId);
        Assert.Equal(3, workspaceUpdate.DependencyLevel);
    }

    [Fact]
    public async Task Edges_into_workspace_role_repository_are_dropped_and_never_make_a_cycle()
    {
        await using var ctx = await ContextScopingTestContext.CreateAsync();
        await using var scope = ctx.CreateScope();
        var (workspaceRepoId, workspaceLinkId) = await AddWorkspaceRoleRepositoryAsync(ctx, scope);
        await AddVersionFileAsync(ctx, scope, workspaceRepoId, "REPO_B={repo-b}");
        // A source version file naming the Workspace repository and a custom dependency on it: both would close a cycle.
        await AddVersionFileAsync(ctx, scope, ctx.RepoBId, "WS={ws-root}");
        var db = ctx.Db(scope);
        db.WorkspaceRepositoryCustomDependencies.Add(new WorkspaceRepositoryCustomDependency
        {
            DependentWorkspaceRepositoryId = ctx.WorkspaceRepositoryAId,
            ReferencedWorkspaceRepositoryId = workspaceLinkId,
        });
        await db.SaveChangesAsync();
        var repo = ctx.Repo(scope);

        await repo.RecomputeAndPersistRepositoryDependencyStatsAsync(ctx.WorkspaceId, ctx.SpecialContextId);

        var graph = await repo.GetRepositoryDependencyGraphAsync(ctx.WorkspaceId, (WorkspaceFeatureContextId?)new WorkspaceFeatureContextId(ctx.SpecialContextId));
        Assert.DoesNotContain(graph.Edges, e => e.ReferencedRepositoryId == workspaceRepoId);
        Assert.Equal(1, (await LinkAsync(ctx, scope, ctx.RepoBId)).DependencyLevel);
        Assert.Equal(2, (await LinkAsync(ctx, scope, ctx.RepoAId)).DependencyLevel);
        Assert.Equal(3, (await LinkAsync(ctx, scope, workspaceRepoId)).DependencyLevel);
        Assert.Equal(0, (await LinkAsync(ctx, scope, ctx.RepoBId)).Dependencies);
    }

    [Fact]
    public async Task Source_project_referencing_workspace_role_repository_is_never_rewritten()
    {
        await using var ctx = await ContextScopingTestContext.CreateAsync();
        await using var scope = ctx.CreateScope();
        var (workspaceRepoId, _) = await AddWorkspaceRoleRepositoryAsync(ctx, scope);
        var workspaceProjectId = await AddProjectReferencingRepoBAsync(ctx, scope, workspaceRepoId, "1.0.0");
        var db = ctx.Db(scope);
        db.ProjectDependencies.Add(new ProjectDependency { DependentProjectId = ctx.WorkspaceProjectAId, ReferencedProjectId = workspaceProjectId, Version = "0.1.0" });
        await db.SaveChangesAsync();
        var repo = ctx.Repo(scope);

        await repo.RecomputeAndPersistRepositoryDependencyStatsAsync(ctx.WorkspaceId, ctx.SpecialContextId);

        Assert.Equal(0, (await LinkAsync(ctx, scope, ctx.RepoAId)).UnmatchedDeps);
        Assert.Equal(3, (await LinkAsync(ctx, scope, workspaceRepoId)).DependencyLevel);
        var payload = await repo.GetSyncDependenciesPayloadAsync(ctx.WorkspaceId, ctx.SpecialContextId);
        Assert.DoesNotContain(payload, p => p.RepoId == ctx.RepoAId);
    }

    [Fact]
    public async Task Custom_dependency_picker_never_offers_workspace_role_repository()
    {
        await using var ctx = await ContextScopingTestContext.CreateAsync();
        await using var scope = ctx.CreateScope();
        var (workspaceRepoId, _) = await AddWorkspaceRoleRepositoryAsync(ctx, scope);
        var repo = ctx.Repo(scope);

        Assert.Contains(workspaceRepoId, await repo.GetCircularCustomDependencyRepoIdsAsync(ctx.WorkspaceId, ctx.RepoBId));
        // The Workspace repository itself may declare custom dependencies on any source repository.
        var fromWorkspace = await repo.GetCircularCustomDependencyRepoIdsAsync(ctx.WorkspaceId, workspaceRepoId);
        Assert.DoesNotContain(ctx.RepoAId, fromWorkspace);
        Assert.DoesNotContain(ctx.RepoBId, fromWorkspace);
    }
}
