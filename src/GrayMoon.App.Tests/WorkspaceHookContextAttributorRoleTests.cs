using GrayMoon.App.Data;
using GrayMoon.Application.Features;
using Microsoft.Extensions.DependencyInjection;

namespace GrayMoon.App.Tests;

public sealed class WorkspaceHookContextAttributorRoleTests
{
    private const string WorkspaceRepoName = "ws-root-repo";

    [Fact]
    public async Task Hook_path_equal_to_workspace_root_attributes_to_special_context_for_workspace_role()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        await using var scope = ctx.CreateScope();
        var attributor = scope.ServiceProvider.GetRequiredService<IWorkspaceHookContextAttributor>();
        var pathResolver = scope.ServiceProvider.GetRequiredService<IWorkspaceContextPathResolver>();
        var special = await ctx.GetSpecialContextIdAsync();
        var workspaceLinkId = await WorkspaceRoleTestHelpers.AddWorkspaceRoleLinkAsync(scope, ctx.WorkspaceId, WorkspaceRepoName);
        var workspaceRepositoryId = await RepositoryIdOfAsync(scope, workspaceLinkId);

        var workspaceRoot = await pathResolver.GetContextRootAsync(special);
        var resolved = await attributor.ResolveAsync(ctx.WorkspaceId, workspaceRepositoryId, workspaceRoot);

        Assert.Equal(special, resolved);
    }

    [Fact]
    public async Task Hook_path_equal_to_feature_root_attributes_to_feature_context_for_workspace_role()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        await using var scope = ctx.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var attributor = scope.ServiceProvider.GetRequiredService<IWorkspaceHookContextAttributor>();
        var pathResolver = scope.ServiceProvider.GetRequiredService<IWorkspaceContextPathResolver>();
        var workspaceLinkId = await WorkspaceRoleTestHelpers.AddWorkspaceRoleLinkAsync(scope, ctx.WorkspaceId, WorkspaceRepoName);
        var workspaceRepositoryId = await RepositoryIdOfAsync(scope, workspaceLinkId);
        var feature = await WorkspaceRoleTestHelpers.CreateFeatureContextAsync(db, ctx.WorkspaceId, "feat-hook-root");

        var featureRoot = await pathResolver.GetContextRootAsync(feature);
        await WorkspaceRoleTestHelpers.AddFeatureRowAsync(db, feature, workspaceLinkId, featureRoot);

        var resolved = await attributor.ResolveAsync(ctx.WorkspaceId, workspaceRepositoryId, featureRoot);

        Assert.Equal(feature, resolved);
    }

    [Fact]
    public async Task Hook_path_of_source_repo_still_attributes_to_source_link_not_workspace_link()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        await using var scope = ctx.CreateScope();
        var attributor = scope.ServiceProvider.GetRequiredService<IWorkspaceHookContextAttributor>();
        var pathResolver = scope.ServiceProvider.GetRequiredService<IWorkspaceContextPathResolver>();
        var special = await ctx.GetSpecialContextIdAsync();
        await WorkspaceRoleTestHelpers.AddWorkspaceRoleLinkAsync(scope, ctx.WorkspaceId, WorkspaceRepoName);

        var sourcePath = await pathResolver.GetRepositoryPathAsync(special, ctx.WorkspaceRepositoryId);
        var workspaceRoot = await pathResolver.GetContextRootAsync(special);

        // The Source repository's own path attributes to the special context through the Source link.
        var resolved = await attributor.ResolveAsync(ctx.WorkspaceId, ctx.RepositoryId, sourcePath);
        Assert.Equal(special, resolved);

        // The Workspace root is not the Source repository's path, so it must not attribute through the Source link.
        var notSource = await attributor.ResolveAsync(ctx.WorkspaceId, ctx.RepositoryId, workspaceRoot);
        Assert.Null(notSource);
    }

    private static async Task<int> RepositoryIdOfAsync(AsyncServiceScope scope, int workspaceRepositoryId)
    {
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var link = await db.WorkspaceRepositories.FindAsync(workspaceRepositoryId);
        return link!.RepositoryId;
    }
}
