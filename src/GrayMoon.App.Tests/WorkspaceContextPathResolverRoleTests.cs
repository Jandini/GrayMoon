using GrayMoon.App.Data;
using GrayMoon.App.Models;
using GrayMoon.App.Services.Features;
using GrayMoon.Application.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GrayMoon.App.Tests;

public sealed class WorkspaceContextPathResolverRoleTests
{
    private const string WorkspaceRepoName = "ws-root-repo";

    [Fact]
    public async Task Special_context_source_role_resolves_to_root_slash_name()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        await using var scope = ctx.CreateScope();
        var pathResolver = scope.ServiceProvider.GetRequiredService<IWorkspaceContextPathResolver>();
        var special = await ctx.GetSpecialContextIdAsync();

        var contextRoot = await pathResolver.GetContextRootAsync(special);
        var path = await pathResolver.GetRepositoryPathAsync(special, ctx.WorkspaceRepositoryId);

        Assert.Equal(WorkerPath.Combine(contextRoot, "graymoon-api"), path);
    }

    [Fact]
    public async Task Special_context_workspace_role_resolves_to_root()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        await using var scope = ctx.CreateScope();
        var pathResolver = scope.ServiceProvider.GetRequiredService<IWorkspaceContextPathResolver>();
        var special = await ctx.GetSpecialContextIdAsync();
        var workspaceLinkId = await WorkspaceRoleTestHelpers.AddWorkspaceRoleLinkAsync(scope, ctx.WorkspaceId, WorkspaceRepoName);

        var contextRoot = await pathResolver.GetContextRootAsync(special);
        var path = await pathResolver.GetRepositoryPathAsync(special, workspaceLinkId);

        Assert.Equal(contextRoot, path);
    }

    [Fact]
    public async Task Feature_context_workspace_role_uses_persisted_worktree_path()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        await using var scope = ctx.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var pathResolver = scope.ServiceProvider.GetRequiredService<IWorkspaceContextPathResolver>();
        var workspaceLinkId = await WorkspaceRoleTestHelpers.AddWorkspaceRoleLinkAsync(scope, ctx.WorkspaceId, WorkspaceRepoName);
        var feature = await WorkspaceRoleTestHelpers.CreateFeatureContextAsync(db, ctx.WorkspaceId, "feat-root-wt");

        const string worktreePath = @"C:\Workspace\.graymoon\test-ws\features\feat-root-wt";
        await WorkspaceRoleTestHelpers.AddFeatureRowAsync(db, feature, workspaceLinkId, worktreePath);

        var path = await pathResolver.GetRepositoryPathAsync(feature, workspaceLinkId);

        Assert.Equal(worktreePath, path);
    }

    [Fact]
    public async Task Feature_context_workspace_role_without_worktree_path_resolves_to_feature_root()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        await using var scope = ctx.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var pathResolver = scope.ServiceProvider.GetRequiredService<IWorkspaceContextPathResolver>();
        var workspaceLinkId = await WorkspaceRoleTestHelpers.AddWorkspaceRoleLinkAsync(scope, ctx.WorkspaceId, WorkspaceRepoName);
        var feature = await WorkspaceRoleTestHelpers.CreateFeatureContextAsync(db, ctx.WorkspaceId, "feat-root-nopath");

        await WorkspaceRoleTestHelpers.AddFeatureRowAsync(db, feature, workspaceLinkId, worktreePath: null);

        var featureRoot = await pathResolver.GetContextRootAsync(feature);
        var path = await pathResolver.GetRepositoryPathAsync(feature, workspaceLinkId);

        Assert.Equal(featureRoot, path);
        Assert.EndsWith("feat-root-nopath", featureRoot, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetWorkerWorkspaceArgs_returns_workspace_repository_name_when_present_else_null()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        await using var scope = ctx.CreateScope();
        var pathResolver = scope.ServiceProvider.GetRequiredService<IWorkspaceContextPathResolver>();
        var special = await ctx.GetSpecialContextIdAsync();

        var without = await pathResolver.GetWorkerArgsAsync(special);
        Assert.Null(without.WorkspaceRepositoryName);

        await WorkspaceRoleTestHelpers.AddWorkspaceRoleLinkAsync(scope, ctx.WorkspaceId, WorkspaceRepoName);

        var with = await pathResolver.GetWorkerArgsAsync(special);
        Assert.Equal(WorkspaceRepoName, with.WorkspaceRepositoryName);
        Assert.Equal(without.WorkspaceRoot, with.WorkspaceRoot);
        Assert.Equal(without.WorkspaceFolderName, with.WorkspaceFolderName);
    }
}

/// <summary>Seeding helpers shared by the Workspace-role resolver and attributor tests.</summary>
internal static class WorkspaceRoleTestHelpers
{
    public static async Task<int> AddWorkspaceRoleLinkAsync(AsyncServiceScope scope, int workspaceId, string repositoryName)
    {
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var connector = await db.Connectors.FirstAsync();
        var repository = new Repository
        {
            ConnectorId = connector.ConnectorId,
            RepositoryName = repositoryName,
            OrgName = "acme",
            Visibility = "Public",
            CloneUrl = "https://github.com/acme/" + repositoryName + ".git",
        };
        db.Repositories.Add(repository);
        await db.SaveChangesAsync();

        var link = new WorkspaceRepositoryLink
        {
            WorkspaceId = workspaceId,
            RepositoryId = repository.RepositoryId,
            Role = WorkspaceRepositoryRole.Workspace,
        };
        db.WorkspaceRepositories.Add(link);
        await db.SaveChangesAsync();
        return link.WorkspaceRepositoryId;
    }

    public static async Task AddFeatureRowAsync(
        AppDbContext db, WorkspaceFeatureContextId feature, int workspaceRepositoryId, string? worktreePath)
    {
        db.WorkspaceFeatureRepositories.Add(new WorkspaceFeatureRepository
        {
            WorkspaceFeatureContextId = feature.Value,
            WorkspaceRepositoryId = workspaceRepositoryId,
            WorktreePath = worktreePath ?? string.Empty,
            BaseCommitSha = "abc123",
            State = worktreePath is null ? WorkspaceFeatureRepositoryState.Pending : WorkspaceFeatureRepositoryState.Ready,
            CreatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
    }

    public static async Task<WorkspaceFeatureContextId> CreateFeatureContextAsync(
        AppDbContext db, int workspaceId, string name)
    {
        var feature = new WorkspaceFeature
        {
            WorkspaceId = workspaceId,
            Name = name,
            LifecycleState = WorkspaceFeatureLifecycleState.Ready,
            BaseKind = WorkspaceFeatureBaseKind.CurrentWorkspace,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.WorkspaceFeatures.Add(feature);
        await db.SaveChangesAsync();

        var context = new WorkspaceFeatureContext
        {
            WorkspaceId = workspaceId,
            Kind = WorkspaceFeatureContextKind.Feature,
            WorkspaceFeatureId = feature.WorkspaceFeatureId,
            CreatedAt = DateTime.UtcNow,
            IsInSync = true
        };
        db.WorkspaceFeatureContexts.Add(context);
        await db.SaveChangesAsync();
        return new WorkspaceFeatureContextId(context.WorkspaceFeatureContextId);
    }
}
