using GrayMoon.App.Data;
using GrayMoon.App.Models;
using GrayMoon.App.Models.Api;
using GrayMoon.Application;
using GrayMoon.Application.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GrayMoon.App.Tests;

/// <summary>
/// Switch Branch Locals: a non-pinned Feature repository always offers the Feature's own branch, even when the
/// repository sits on another branch and the shared RepositoryBranches rows do not list it.
/// </summary>
public sealed class WorkspaceBranchOperationsFeatureBranchListTests
{
    private const string FeatureName = "feat-a";

    [Fact]
    public async Task Feature_repository_on_another_branch_still_lists_the_Feature_branch()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        await SeedSharedBranchesAsync(ctx, "main");
        var featureContext = await SeedFeatureAsync(ctx, pinnedTag: null, repositoryState: WorkspaceFeatureRepositoryState.Ready, currentBranch: "main");

        var snapshot = await GetSnapshotAsync(ctx, featureContext);

        Assert.Equal(["feat-a", "main"], snapshot.LocalBranches);
        Assert.Equal("main", snapshot.CurrentBranch);
    }

    [Fact]
    public async Task Feature_branch_is_not_duplicated_when_shared_rows_already_list_it_in_another_case()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        await SeedSharedBranchesAsync(ctx, "main", "FEAT-A");
        var featureContext = await SeedFeatureAsync(ctx, pinnedTag: null, repositoryState: WorkspaceFeatureRepositoryState.Ready, currentBranch: "main");

        var snapshot = await GetSnapshotAsync(ctx, featureContext);

        Assert.Equal(["FEAT-A", "main"], snapshot.LocalBranches);
    }

    [Fact]
    public async Task Pinned_Feature_repository_does_not_get_a_Feature_branch()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        await SeedSharedBranchesAsync(ctx, "main");
        var featureContext = await SeedFeatureAsync(ctx, pinnedTag: "1.0.0", repositoryState: WorkspaceFeatureRepositoryState.Ready, currentBranch: null);

        var snapshot = await GetSnapshotAsync(ctx, featureContext);

        Assert.Equal(["main"], snapshot.LocalBranches);
    }

    [Theory]
    [InlineData(WorkspaceFeatureRepositoryState.Pending)]
    [InlineData(WorkspaceFeatureRepositoryState.Removing)]
    [InlineData(WorkspaceFeatureRepositoryState.Removed)]
    public async Task Feature_repository_without_a_live_worktree_does_not_get_a_Feature_branch(WorkspaceFeatureRepositoryState state)
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        await SeedSharedBranchesAsync(ctx, "main");
        var featureContext = await SeedFeatureAsync(ctx, pinnedTag: null, repositoryState: state, currentBranch: "main");

        var snapshot = await GetSnapshotAsync(ctx, featureContext);

        Assert.Equal(["main"], snapshot.LocalBranches);
    }

    [Fact]
    public async Task Workspace_context_lists_only_the_shared_rows()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        await SeedSharedBranchesAsync(ctx, "main", "topic");
        await SeedFeatureAsync(ctx, pinnedTag: null, repositoryState: WorkspaceFeatureRepositoryState.Ready, currentBranch: "main");
        var special = await ctx.GetSpecialContextIdAsync();

        var snapshot = await GetSnapshotAsync(ctx, special);

        Assert.Equal(["main", "topic"], snapshot.LocalBranches);
        Assert.Equal(["origin/main"], snapshot.RemoteBranches);
        Assert.Equal("feature/x", snapshot.CurrentBranch);
    }

    private static async Task<WorkspaceBranchesSnapshot> GetSnapshotAsync(SyncStateTestContext ctx, WorkspaceFeatureContextId contextId)
    {
        await using var scope = ctx.CreateScope();
        var ops = scope.ServiceProvider.GetRequiredService<IWorkspaceBranchOperations>();

        var outcome = await ops.GetBranchesAsync(ctx.WorkspaceId, contextId, ctx.RepositoryId);

        Assert.True(outcome.IsSuccessStatus);
        return Assert.IsType<WorkspaceBranchesSnapshot>(outcome.Body);
    }

    private static async Task SeedSharedBranchesAsync(SyncStateTestContext ctx, params string[] localBranches)
    {
        var factory = ctx.Resolve<IDbContextFactory<AppDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        foreach (var name in localBranches)
        {
            db.RepositoryBranches.Add(new RepositoryBranch
            {
                WorkspaceRepositoryId = ctx.WorkspaceRepositoryId,
                BranchName = name,
                LastSeenAt = DateTime.UtcNow
            });
        }
        db.RepositoryBranches.Add(new RepositoryBranch
        {
            WorkspaceRepositoryId = ctx.WorkspaceRepositoryId,
            BranchName = "origin/main",
            IsRemote = true,
            LastSeenAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
    }

    private static async Task<WorkspaceFeatureContextId> SeedFeatureAsync(
        SyncStateTestContext ctx,
        string? pinnedTag,
        WorkspaceFeatureRepositoryState repositoryState,
        string? currentBranch)
    {
        var factory = ctx.Resolve<IDbContextFactory<AppDbContext>>();
        await using var db = await factory.CreateDbContextAsync();

        var feature = new WorkspaceFeature
        {
            WorkspaceId = ctx.WorkspaceId,
            Name = FeatureName,
            LifecycleState = WorkspaceFeatureLifecycleState.Ready,
            BaseKind = WorkspaceFeatureBaseKind.CurrentWorkspace,
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
            CreatedAt = DateTime.UtcNow,
            IsInSync = true
        };
        db.WorkspaceFeatureContexts.Add(context);
        await db.SaveChangesAsync();

        db.WorkspaceFeatureRepositories.Add(new WorkspaceFeatureRepository
        {
            WorkspaceFeatureContextId = context.WorkspaceFeatureContextId,
            WorkspaceRepositoryId = ctx.WorkspaceRepositoryId,
            WorktreePath = @"C:\gm-test-root\.graymoon\test-ws\features\feat-a\graymoon-api",
            State = repositoryState,
            BaseCommitSha = "abc123",
            PinnedTag = pinnedTag,
            CreatedAt = DateTime.UtcNow
        });
        db.WorkspaceRepositoryContextStates.Add(new WorkspaceRepositoryContextState
        {
            WorkspaceFeatureContextId = context.WorkspaceFeatureContextId,
            WorkspaceRepositoryId = ctx.WorkspaceRepositoryId,
            BranchName = currentBranch,
            CheckedOutTag = pinnedTag
        });
        await db.SaveChangesAsync();

        return new WorkspaceFeatureContextId(context.WorkspaceFeatureContextId);
    }
}
