using GrayMoon.Abstractions.Agent;
using GrayMoon.App.Data;
using GrayMoon.App.Models;
using GrayMoon.App.Services.Features;
using GrayMoon.Application.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GrayMoon.App.Tests;

/// <summary>
/// I2: a Feature's own branch is always checkable from the Switch Branch dialog, even when it is
/// not currently listed in <c>git worktree list</c> (09 SB-1). Other Features' branches, and the
/// special Workspace's own view, keep their original "owned, blocked" behavior.
/// </summary>
public sealed class WorkspaceBranchOccupancyServiceFeatureOwnTests
{
    private const string FeatureAName = "feat-a";
    private const string FeatureBName = "feat-b";

    [Fact]
    public async Task Viewing_feature_a_its_own_branch_is_FeatureOwn_and_checkable_while_feature_b_stays_blocked()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync(
            configureServices: s => s.AddScoped<IWorkspaceBranchOccupancyService, WorkspaceBranchOccupancyService>());
        await using var scope = ctx.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var contextA = await CreateFeatureContextAsync(db, ctx.WorkspaceId, FeatureAName);
        var contextB = await CreateFeatureContextAsync(db, ctx.WorkspaceId, FeatureBName);
        await SeedFeatureRepositoryAsync(db, contextA, ctx.WorkspaceRepositoryId, @"C:\gm-test-root\.graymoon\test-ws\features\feat-a\graymoon-api");
        await SeedFeatureRepositoryAsync(db, contextB, ctx.WorkspaceRepositoryId, @"C:\gm-test-root\.graymoon\test-ws\features\feat-b\graymoon-api");

        // Neither Feature's branch is currently checked out anywhere - both repositories have
        // drifted off their own branch, which is exactly when the special-case second loop fires.
        ctx.AgentBridge.Respond(AgentHubMethods.ListGitWorktrees, new
        {
            worktrees = new[]
            {
                new { worktreePath = @"C:\gm-test-root\graymoon-api", branchName = "main", isBare = false },
            },
        });

        var service = scope.ServiceProvider.GetRequiredService<IWorkspaceBranchOccupancyService>();
        var badges = await service.GetBadgesForRepositoryAsync(ctx.WorkspaceId, ctx.WorkspaceRepositoryId, contextA);

        Assert.True(badges.TryGetValue(FeatureAName, out var own));
        Assert.Equal(BranchOccupancyKind.FeatureOwn, own!.Kind);
        Assert.True(own.AllowCheckout);
        Assert.False(own.RequiresFeatureCleanup);
        Assert.Null(own.ContextId);

        Assert.True(badges.TryGetValue(FeatureBName, out var other));
        Assert.Equal(BranchOccupancyKind.Feature, other!.Kind);
        Assert.False(other.AllowCheckout);
        Assert.True(other.RequiresFeatureCleanup);
        Assert.Equal(contextB.Value, other.ContextId?.Value);
    }

    /// <summary>A4 step 0 characterization: the special Workspace's own view of two Features' branches
    /// is unchanged by the I2 fix (both stay "Feature", blocked, routed to Remove Feature).</summary>
    [Fact]
    public async Task Viewing_the_workspace_both_feature_branches_stay_Feature_and_blocked()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync(
            configureServices: s => s.AddScoped<IWorkspaceBranchOccupancyService, WorkspaceBranchOccupancyService>());
        await using var scope = ctx.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var contextA = await CreateFeatureContextAsync(db, ctx.WorkspaceId, FeatureAName);
        var contextB = await CreateFeatureContextAsync(db, ctx.WorkspaceId, FeatureBName);
        await SeedFeatureRepositoryAsync(db, contextA, ctx.WorkspaceRepositoryId, @"C:\gm-test-root\.graymoon\test-ws\features\feat-a\graymoon-api");
        await SeedFeatureRepositoryAsync(db, contextB, ctx.WorkspaceRepositoryId, @"C:\gm-test-root\.graymoon\test-ws\features\feat-b\graymoon-api");

        ctx.AgentBridge.Respond(AgentHubMethods.ListGitWorktrees, new
        {
            worktrees = new[]
            {
                new { worktreePath = @"C:\gm-test-root\graymoon-api", branchName = "main", isBare = false },
            },
        });

        var service = scope.ServiceProvider.GetRequiredService<IWorkspaceBranchOccupancyService>();
        var special = await ctx.GetSpecialContextIdAsync();
        var badges = await service.GetBadgesForRepositoryAsync(ctx.WorkspaceId, ctx.WorkspaceRepositoryId, special);

        Assert.True(badges.TryGetValue(FeatureAName, out var a));
        Assert.Equal(BranchOccupancyKind.Feature, a!.Kind);
        Assert.False(a.AllowCheckout);
        Assert.True(a.RequiresFeatureCleanup);
        Assert.Equal(contextA.Value, a.ContextId?.Value);

        Assert.True(badges.TryGetValue(FeatureBName, out var b));
        Assert.Equal(BranchOccupancyKind.Feature, b!.Kind);
        Assert.False(b.AllowCheckout);
        Assert.True(b.RequiresFeatureCleanup);
        Assert.Equal(contextB.Value, b.ContextId?.Value);
    }

    private static async Task<WorkspaceFeatureContextId> CreateFeatureContextAsync(
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

    private static async Task SeedFeatureRepositoryAsync(
        AppDbContext db, WorkspaceFeatureContextId contextId, int workspaceRepositoryId, string worktreePath)
    {
        db.WorkspaceFeatureRepositories.Add(new WorkspaceFeatureRepository
        {
            WorkspaceFeatureContextId = contextId.Value,
            WorkspaceRepositoryId = workspaceRepositoryId,
            WorktreePath = worktreePath,
            State = WorkspaceFeatureRepositoryState.Ready,
            BaseCommitSha = "abc123",
            CreatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
    }
}
