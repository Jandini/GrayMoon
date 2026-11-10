using GrayMoon.App.Data;
using GrayMoon.App.Models;
using GrayMoon.App.Models.Api;
using GrayMoon.App.Services;
using GrayMoon.App.Services.Application;
using GrayMoon.App.Services.Git;
using GrayMoon.App.Services.Workspaces;
using GrayMoon.Application.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GrayMoon.App.Tests;

/// <summary>
/// A branch refresh, a remote branch delete or a package restore that runs in a Feature context reads and writes
/// that Feature's own state. The shared <see cref="WorkspaceRepositoryLink"/> (what the special Workspace shows)
/// is seeded on <c>feature/x</c> with an upstream and must come out unchanged.
/// </summary>
public sealed class FeatureContextBranchRefreshScopingTests
{
    private const string FeatureBranch = "feat-scope";

    private static async Task<WorkspaceFeatureContextId> SeedFeatureAsync(SyncStateTestContext ctx, Action<WorkspaceRepositoryContextState>? configure = null)
    {
        await using var scope = ctx.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var feature = new WorkspaceFeature
        {
            WorkspaceId = ctx.WorkspaceId,
            Name = FeatureBranch,
            LifecycleState = WorkspaceFeatureLifecycleState.Ready,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.WorkspaceFeatures.Add(feature);
        await db.SaveChangesAsync();

        var context = new WorkspaceFeatureContext
        {
            WorkspaceId = ctx.WorkspaceId,
            Kind = WorkspaceFeatureContextKind.Feature,
            WorkspaceFeatureId = feature.WorkspaceFeatureId,
            CreatedAt = DateTime.UtcNow
        };
        db.WorkspaceFeatureContexts.Add(context);
        await db.SaveChangesAsync();

        db.WorkspaceFeatureRepositories.Add(new WorkspaceFeatureRepository
        {
            WorkspaceFeatureContextId = context.WorkspaceFeatureContextId,
            WorkspaceRepositoryId = ctx.WorkspaceRepositoryId,
            WorktreePath = @"C:\gm-features\feat-scope\api",
            BaseCommitSha = "abc123",
            CreatedAt = DateTime.UtcNow,
            State = WorkspaceFeatureRepositoryState.Ready
        });
        var state = new WorkspaceRepositoryContextState
        {
            WorkspaceFeatureContextId = context.WorkspaceFeatureContextId,
            WorkspaceRepositoryId = ctx.WorkspaceRepositoryId,
            BranchName = FeatureBranch,
            BranchHasUpstream = true,
            OutgoingCommits = 1,
            SyncStatus = RepoSyncStatus.InSync
        };
        configure?.Invoke(state);
        db.WorkspaceRepositoryContextStates.Add(state);
        await db.SaveChangesAsync();

        return new WorkspaceFeatureContextId(context.WorkspaceFeatureContextId);
    }

    private static async Task<WorkspaceRepositoryContextState> ReadStateAsync(SyncStateTestContext ctx, WorkspaceFeatureContextId contextId)
    {
        await using var scope = ctx.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.WorkspaceRepositoryContextStates.AsNoTracking().SingleAsync(s =>
            s.WorkspaceFeatureContextId == contextId.Value && s.WorkspaceRepositoryId == ctx.WorkspaceRepositoryId);
    }

    private static BranchesResponse OnTag(string currentTag) => new()
    {
        Success = true,
        LocalBranches = ["main"],
        RemoteBranches = ["main"],
        DefaultBranch = "main",
        Tags = ["v2.0.0", "v1.0.0"],
        CurrentTag = currentTag
    };

    private static BranchesResponse OnBranch(string branch, bool hasUpstream) => new()
    {
        Success = true,
        LocalBranches = ["main", branch],
        RemoteBranches = ["main"],
        CurrentBranch = branch,
        DefaultBranch = "main",
        Tags = ["v2.0.0", "v1.0.0"],
        UpstreamProbed = true,
        HasUpstream = hasUpstream
    };

    private static async Task AssertSharedLinkUnchangedAsync(SyncStateTestContext ctx)
    {
        var link = await ctx.ReadLinkAsync();
        Assert.Equal("feature/x", link.BranchName);
        Assert.Null(link.CheckedOutTag);
        Assert.Null(link.HasNewerTag);
        Assert.True(link.BranchHasUpstream);
        Assert.Equal(3, link.OutgoingCommits);
        Assert.Equal(2, link.IncomingCommits);
    }

    [Fact]
    public async Task Branch_refresh_of_a_Feature_on_a_tag_records_the_tag_on_the_Feature_only()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        var featureId = await SeedFeatureAsync(ctx);
        ctx.WorkerBridge.Respond("RefreshBranches", OnTag("v1.0.0"));

        await using (var scope = ctx.CreateScope())
        {
            var operations = scope.ServiceProvider.GetRequiredService<IWorkspaceBranchOperations>();
            var outcome = await operations.RefreshBranchesAsync(ctx.WorkspaceId, featureId, ctx.RepositoryId);
            Assert.Equal(200, outcome.StatusCode);
        }

        await AssertSharedLinkUnchangedAsync(ctx);
        var state = await ReadStateAsync(ctx, featureId);
        Assert.Equal("v1.0.0", state.CheckedOutTag);
        Assert.True(state.HasNewerTag);
        Assert.Null(state.BranchName);
        Assert.Null(state.BranchHasUpstream);
        Assert.Null(state.OutgoingCommits);
    }

    [Fact]
    public async Task Branch_refresh_of_a_Feature_writes_the_upstream_flag_to_the_Feature_only()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        var featureId = await SeedFeatureAsync(ctx);
        ctx.WorkerBridge.Respond("RefreshBranches", OnBranch(FeatureBranch, hasUpstream: false));

        await using (var scope = ctx.CreateScope())
        {
            var operations = scope.ServiceProvider.GetRequiredService<IWorkspaceBranchOperations>();
            await operations.RefreshBranchesAsync(ctx.WorkspaceId, featureId, ctx.RepositoryId);
        }

        await AssertSharedLinkUnchangedAsync(ctx);
        var state = await ReadStateAsync(ctx, featureId);
        Assert.False(state.BranchHasUpstream);
        Assert.Equal(FeatureBranch, state.BranchName);
    }

    [Fact]
    public async Task Sync_time_branch_refresh_of_a_Feature_leaves_the_shared_link_alone()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        var featureId = await SeedFeatureAsync(ctx);

        ctx.WorkerBridge.Respond("RefreshBranches", OnBranch(FeatureBranch, hasUpstream: false));
        await using (var scope = ctx.CreateScope())
        {
            var git = scope.ServiceProvider.GetRequiredService<WorkspaceGitService>();
            Assert.True(await git.RefreshBranchesForRepositoryAsync(ctx.RepositoryId, ctx.WorkspaceId, featureId));
        }

        await AssertSharedLinkUnchangedAsync(ctx);
        Assert.False((await ReadStateAsync(ctx, featureId)).BranchHasUpstream);

        ctx.WorkerBridge.Respond("RefreshBranches", OnTag("v2.0.0"));
        await using (var scope = ctx.CreateScope())
        {
            var git = scope.ServiceProvider.GetRequiredService<WorkspaceGitService>();
            Assert.True(await git.RefreshBranchesForRepositoryAsync(ctx.RepositoryId, ctx.WorkspaceId, featureId));
        }

        await AssertSharedLinkUnchangedAsync(ctx);
        var state = await ReadStateAsync(ctx, featureId);
        Assert.Equal("v2.0.0", state.CheckedOutTag);
        Assert.False(state.HasNewerTag);
    }

    [Fact]
    public async Task Deleting_the_remote_branch_of_a_Feature_clears_the_upstream_flag_of_the_Feature_only()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        var featureId = await SeedFeatureAsync(ctx);
        ctx.WorkerBridge.Respond("DeleteBranch", new { success = true });

        await using (var scope = ctx.CreateScope())
        {
            var operations = scope.ServiceProvider.GetRequiredService<IWorkspaceBranchOperations>();
            var outcome = await operations.DeleteBranchAsync(ctx.WorkspaceId, featureId, ctx.RepositoryId, FeatureBranch, isRemote: true, force: false);
            Assert.Equal(200, outcome.StatusCode);
        }

        await AssertSharedLinkUnchangedAsync(ctx);
        Assert.False((await ReadStateAsync(ctx, featureId)).BranchHasUpstream);
    }

    [Fact]
    public async Task Branch_refresh_of_the_special_Workspace_still_updates_the_shared_link()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        var featureId = await SeedFeatureAsync(ctx);
        var specialId = await ctx.GetSpecialContextIdAsync();
        ctx.WorkerBridge.Respond("RefreshBranches", OnTag("v1.0.0"));

        await using (var scope = ctx.CreateScope())
        {
            var operations = scope.ServiceProvider.GetRequiredService<IWorkspaceBranchOperations>();
            await operations.RefreshBranchesAsync(ctx.WorkspaceId, specialId, ctx.RepositoryId);
        }

        var link = await ctx.ReadLinkAsync();
        Assert.Equal("v1.0.0", link.CheckedOutTag);
        Assert.True(link.HasNewerTag);
        Assert.Null(link.BranchName);
        Assert.Null(link.BranchHasUpstream);

        // The Feature is a different checkout: the Workspace moving to a tag says nothing about it.
        var state = await ReadStateAsync(ctx, featureId);
        Assert.Equal(FeatureBranch, state.BranchName);
        Assert.Null(state.CheckedOutTag);
        Assert.True(state.BranchHasUpstream);

        ctx.WorkerBridge.Respond("RefreshBranches", OnBranch("feature/x", hasUpstream: false));
        await using (var scope = ctx.CreateScope())
        {
            var operations = scope.ServiceProvider.GetRequiredService<IWorkspaceBranchOperations>();
            await operations.RefreshBranchesAsync(ctx.WorkspaceId, specialId, ctx.RepositoryId);
        }

        link = await ctx.ReadLinkAsync();
        Assert.Null(link.CheckedOutTag);
        Assert.Null(link.HasNewerTag);
        Assert.False(link.BranchHasUpstream);
    }

    [Fact]
    public async Task Restore_skips_the_repositories_the_selected_context_has_on_a_tag()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        var featureId = await SeedFeatureAsync(ctx);
        var specialId = await ctx.GetSpecialContextIdAsync();

        // The Workspace is pinned to a tag; the Feature is on its branch and must still be restored.
        await ctx.MutateLinkAsync(l =>
        {
            l.CheckedOutTag = "v1.0.0";
            l.BranchName = null;
        });

        await using (var scope = ctx.CreateScope())
        {
            var git = scope.ServiceProvider.GetRequiredService<WorkspaceGitService>();
            Assert.Empty(await git.GetTagPinnedRepositoryIdsAsync(ctx.WorkspaceId, featureId, CancellationToken.None));
            Assert.Equal([ctx.RepositoryId], await git.GetTagPinnedRepositoryIdsAsync(ctx.WorkspaceId, specialId, CancellationToken.None));
        }

        // The other way round: the Feature is pinned, the Workspace is on a branch.
        await ctx.MutateLinkAsync(l =>
        {
            l.CheckedOutTag = null;
            l.BranchName = "feature/x";
        });
        await using (var scope = ctx.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var state = await db.WorkspaceRepositoryContextStates.SingleAsync(s =>
                s.WorkspaceFeatureContextId == featureId.Value && s.WorkspaceRepositoryId == ctx.WorkspaceRepositoryId);
            state.CheckedOutTag = "v2.0.0";
            state.BranchName = null;
            await db.SaveChangesAsync();
        }

        await using (var scope = ctx.CreateScope())
        {
            var git = scope.ServiceProvider.GetRequiredService<WorkspaceGitService>();
            Assert.Equal([ctx.RepositoryId], await git.GetTagPinnedRepositoryIdsAsync(ctx.WorkspaceId, featureId, CancellationToken.None));
            Assert.Empty(await git.GetTagPinnedRepositoryIdsAsync(ctx.WorkspaceId, specialId, CancellationToken.None));
        }
    }
}
