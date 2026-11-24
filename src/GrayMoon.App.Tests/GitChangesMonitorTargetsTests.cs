using GrayMoon.App.Data;
using GrayMoon.App.Models;
using GrayMoon.App.Services.GitChanges;
using GrayMoon.Application.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GrayMoon.App.Tests;

/// <summary>
/// Which folders the Git Changes scan and the watcher renewal address for a context. A Remove Feature that failed
/// part-way leaves repositories whose worktree is already deleted (state Removed) until Remove is run again; the
/// periodic audit asked the Worker about each of them and logged "Repository not found" 39 times (logs of
/// 2026-11-24, 23:13:16).
/// </summary>
public sealed class GitChangesMonitorTargetsTests
{
    private const string WorktreePath = @"C:\Workspace\.graymoon\test-ws\features\feat\graymoon-api";

    [Theory]
    [InlineData(WorkspaceFeatureRepositoryState.Ready, true)]
    [InlineData(WorkspaceFeatureRepositoryState.Removing, true)]
    [InlineData(WorkspaceFeatureRepositoryState.Removed, false)]
    public async Task A_feature_repository_is_a_target_until_its_worktree_is_removed(
        WorkspaceFeatureRepositoryState state, bool expected)
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        await using var scope = ctx.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var feature = await SeedFeatureAsync(db, ctx, state);

        var targets = await GitChangesMonitorTargets.ResolveAsync(
            db, scope.ServiceProvider.GetRequiredService<IWorkspaceContextPathResolver>(),
            ctx.WorkspaceId, feature, repositoryIds: null, CancellationToken.None);

        Assert.Equal(expected, targets.Any(t => t.RepositoryId == ctx.RepositoryId));
    }

    [Fact]
    public async Task A_removed_feature_worktree_does_not_hide_the_workspace_checkout()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        await using var scope = ctx.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await SeedFeatureAsync(db, ctx, WorkspaceFeatureRepositoryState.Removed);
        var special = await ctx.GetSpecialContextIdAsync();

        var targets = await GitChangesMonitorTargets.ResolveAsync(
            db, scope.ServiceProvider.GetRequiredService<IWorkspaceContextPathResolver>(),
            ctx.WorkspaceId, special, repositoryIds: null, CancellationToken.None);

        Assert.Contains(targets, t => t.RepositoryId == ctx.RepositoryId);
    }

    private static async Task<WorkspaceFeatureContextId> SeedFeatureAsync(
        AppDbContext db, SyncStateTestContext ctx, WorkspaceFeatureRepositoryState state)
    {
        var workspace = await db.Workspaces.FirstAsync(w => w.WorkspaceId == ctx.WorkspaceId);
        workspace.RootPath = @"C:\Workspace";
        workspace.ManagedFeatureStorageRoot = @"C:\Workspace\.graymoon\test-ws\features";

        var feature = new WorkspaceFeature
        {
            WorkspaceId = ctx.WorkspaceId,
            Name = "feat",
            LifecycleState = WorkspaceFeatureLifecycleState.NeedsRepair,
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
            WorktreePath = WorktreePath,
            State = state,
            BaseCommitSha = "abc123",
            CreatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
        return new WorkspaceFeatureContextId(context.WorkspaceFeatureContextId);
    }
}
