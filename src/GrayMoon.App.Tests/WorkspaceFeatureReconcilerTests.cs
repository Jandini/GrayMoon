using GrayMoon.Abstractions.Agent;
using GrayMoon.App.Data;
using GrayMoon.App.Models;
using GrayMoon.App.Services.Features;
using GrayMoon.App.Services.Jobs;
using GrayMoon.Application.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GrayMoon.App.Tests;

/// <summary>C2: reconcile stuck Features and worktree inventory on Worker connect.</summary>
public sealed class WorkspaceFeatureReconcilerTests
{
    private const string StorageRoot = @"C:\Users\test\.graymoon\test-ws\features";
    private const string FeatureName = "feat-reconcile";
    private const string WorktreePath = @"C:\Users\test\.graymoon\test-ws\features\feat-reconcile\graymoon-api";

    [Fact]
    public async Task Stuck_Creating_becomes_NeedsRepair()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        await SeedFeatureAsync(ctx, WorkspaceFeatureLifecycleState.Creating, WorkspaceFeatureRepositoryState.Pending);
        StubHealthyList(ctx);

        await ReconcileAsync(ctx);

        var feature = await ReadFeatureAsync(ctx);
        Assert.Equal(WorkspaceFeatureLifecycleState.NeedsRepair, feature.LifecycleState);
        Assert.Equal(WorkspaceFeatureReconciler.InterruptedCreating, feature.LastError);
    }

    [Fact]
    public async Task Stuck_Removing_becomes_NeedsRepair()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        await SeedFeatureAsync(ctx, WorkspaceFeatureLifecycleState.Removing, WorkspaceFeatureRepositoryState.Removing);
        StubHealthyList(ctx);

        await ReconcileAsync(ctx);

        var feature = await ReadFeatureAsync(ctx);
        Assert.Equal(WorkspaceFeatureLifecycleState.NeedsRepair, feature.LifecycleState);
        Assert.Equal(WorkspaceFeatureReconciler.InterruptedRemoving, feature.LastError);
    }

    [Fact]
    public async Task Running_structural_operation_leaves_Creating_alone()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        await SeedFeatureAsync(ctx, WorkspaceFeatureLifecycleState.Creating, WorkspaceFeatureRepositoryState.Pending);

        var runner = ctx.Resolve<IWorkspaceOperationLock>();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(runner.TryStartStructural(
            ctx.WorkspaceId,
            "create-feature",
            "overlay",
            "Creating...",
            async (_, _) => await release.Task,
            out _));

        StubHealthyList(ctx);
        await ReconcileAsync(ctx);

        var feature = await ReadFeatureAsync(ctx);
        Assert.Equal(WorkspaceFeatureLifecycleState.Creating, feature.LifecycleState);
        Assert.Null(feature.LastError);

        release.SetResult();
    }

    [Fact]
    public async Task Ready_repo_with_unregistered_missing_worktree_becomes_NeedsRepair()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        await SeedFeatureAsync(ctx, WorkspaceFeatureLifecycleState.Ready, WorkspaceFeatureRepositoryState.Ready);
        ctx.AgentBridge.Respond(AgentHubMethods.ListGitWorktrees, new
        {
            worktrees = new[]
            {
                new { worktreePath = @"C:\gm-test-root\test-ws\graymoon-api", branchName = "main", isBare = false },
            },
        });
        ctx.AgentBridge.Respond(AgentHubMethods.InspectWorktree, new
        {
            exists = false,
            isDirty = (bool?)null,
            hasUpstream = (bool?)null,
            aheadOfUpstream = (int?)null,
            aheadOfDefault = (int?)null,
        });

        await ReconcileAsync(ctx);

        var feature = await ReadFeatureAsync(ctx);
        var row = await ReadRepoRowAsync(ctx);
        Assert.Equal(WorkspaceFeatureLifecycleState.NeedsRepair, feature.LifecycleState);
        Assert.Equal(WorkspaceFeatureReconciler.WorktreeMissing, feature.LastError);
        Assert.Equal(WorkspaceFeatureRepositoryState.NeedsRepair, row.State);
        Assert.Equal(WorkspaceFeatureReconciler.WorktreeMissing, row.LastError);
    }

    [Fact]
    public async Task Removed_row_with_unregistered_worktree_stays_Removed()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        await SeedFeatureAsync(ctx, WorkspaceFeatureLifecycleState.NeedsRepair, WorkspaceFeatureRepositoryState.Removed);
        ctx.AgentBridge.Respond(AgentHubMethods.ListGitWorktrees, new
        {
            worktrees = new[]
            {
                new { worktreePath = @"C:\gm-test-root\test-ws\graymoon-api", branchName = "main", isBare = false },
            },
        });
        ctx.AgentBridge.Respond(AgentHubMethods.InspectWorktree, new { exists = false });

        await ReconcileAsync(ctx);

        var row = await ReadRepoRowAsync(ctx);
        Assert.Equal(WorkspaceFeatureRepositoryState.Removed, row.State);
        Assert.DoesNotContain(ctx.AgentBridge.Calls, c => c.Command == AgentHubMethods.InspectWorktree);
    }

    [Fact]
    public async Task Untracked_worktree_under_Feature_root_sets_NeedsRepair_and_is_not_removed()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        await SeedFeatureAsync(ctx, WorkspaceFeatureLifecycleState.Ready, WorkspaceFeatureRepositoryState.Ready);
        const string orphan = @"C:\Users\test\.graymoon\test-ws\features\feat-reconcile\orphan-repo";
        ctx.AgentBridge.Respond(AgentHubMethods.ListGitWorktrees, new
        {
            worktrees = new object[]
            {
                new { worktreePath = @"C:\gm-test-root\test-ws\graymoon-api", branchName = "main", isBare = false },
                new { worktreePath = WorktreePath, branchName = FeatureName, isBare = false },
                new { worktreePath = orphan, branchName = "orphan", isBare = false },
            },
        });

        await ReconcileAsync(ctx);

        var feature = await ReadFeatureAsync(ctx);
        Assert.Equal(WorkspaceFeatureLifecycleState.NeedsRepair, feature.LifecycleState);
        Assert.Equal(WorkspaceFeatureReconciler.UntrackedWorktreeError(orphan), feature.LastError);
        Assert.DoesNotContain(ctx.AgentBridge.Calls, c => c.Command == AgentHubMethods.RemoveGitWorktree);
    }

    [Fact]
    public async Task Untracked_worktree_elsewhere_under_storage_root_does_not_change_Feature()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        await SeedFeatureAsync(ctx, WorkspaceFeatureLifecycleState.Ready, WorkspaceFeatureRepositoryState.Ready);
        const string stray = @"C:\Users\test\.graymoon\test-ws\features\stray-folder\repo";
        ctx.AgentBridge.Respond(AgentHubMethods.ListGitWorktrees, new
        {
            worktrees = new object[]
            {
                new { worktreePath = @"C:\gm-test-root\test-ws\graymoon-api", branchName = "main", isBare = false },
                new { worktreePath = WorktreePath, branchName = FeatureName, isBare = false },
                new { worktreePath = stray, branchName = "stray", isBare = false },
            },
        });

        await ReconcileAsync(ctx);

        var feature = await ReadFeatureAsync(ctx);
        Assert.Equal(WorkspaceFeatureLifecycleState.Ready, feature.LifecycleState);
        Assert.Null(feature.LastError);
        var row = await ReadRepoRowAsync(ctx);
        Assert.Equal(WorkspaceFeatureRepositoryState.Ready, row.State);
    }

    [Fact]
    public async Task Ready_repo_registered_at_path_but_on_another_branch_stays_Ready()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        await SeedFeatureAsync(ctx, WorkspaceFeatureLifecycleState.Ready, WorkspaceFeatureRepositoryState.Ready);
        ctx.AgentBridge.Respond(AgentHubMethods.ListGitWorktrees, new
        {
            worktrees = new[]
            {
                new { worktreePath = WorktreePath, branchName = "some-other-branch", isBare = false },
            },
        });

        await ReconcileAsync(ctx);

        var feature = await ReadFeatureAsync(ctx);
        var row = await ReadRepoRowAsync(ctx);
        Assert.Equal(WorkspaceFeatureLifecycleState.Ready, feature.LifecycleState);
        Assert.Equal(WorkspaceFeatureRepositoryState.Ready, row.State);
        Assert.DoesNotContain(ctx.AgentBridge.Calls, c => c.Command == AgentHubMethods.InspectWorktree);
    }

    [Fact]
    public async Task Pending_repo_with_registered_worktree_becomes_Ready()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        await SeedFeatureAsync(ctx, WorkspaceFeatureLifecycleState.NeedsRepair, WorkspaceFeatureRepositoryState.Pending);
        ctx.AgentBridge.Respond(AgentHubMethods.ListGitWorktrees, new
        {
            worktrees = new[]
            {
                new { worktreePath = WorktreePath, branchName = FeatureName, isBare = false },
            },
        });

        await ReconcileAsync(ctx);

        var row = await ReadRepoRowAsync(ctx);
        Assert.Equal(WorkspaceFeatureRepositoryState.Ready, row.State);
        Assert.Null(row.LastError);
    }

    [Fact]
    public async Task Worker_not_connected_makes_no_changes()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        await SeedFeatureAsync(ctx, WorkspaceFeatureLifecycleState.Creating, WorkspaceFeatureRepositoryState.Pending);
        ctx.AgentBridge.IsAgentConnected = false;

        await ReconcileAsync(ctx);

        var feature = await ReadFeatureAsync(ctx);
        Assert.Equal(WorkspaceFeatureLifecycleState.Creating, feature.LifecycleState);
        Assert.Empty(ctx.AgentBridge.Calls);
    }

    [Fact]
    public async Task ListGitWorktrees_failure_makes_no_row_changes()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        await SeedFeatureAsync(ctx, WorkspaceFeatureLifecycleState.Ready, WorkspaceFeatureRepositoryState.Ready);
        ctx.AgentBridge.Respond(AgentHubMethods.ListGitWorktrees, null, success: false, error: "git failed");

        await ReconcileAsync(ctx);

        var feature = await ReadFeatureAsync(ctx);
        var row = await ReadRepoRowAsync(ctx);
        Assert.Equal(WorkspaceFeatureLifecycleState.Ready, feature.LifecycleState);
        Assert.Equal(WorkspaceFeatureRepositoryState.Ready, row.State);
        Assert.DoesNotContain(ctx.AgentBridge.Calls, c => c.Command == AgentHubMethods.InspectWorktree);
    }

    [Fact]
    public async Task Two_reconciles_within_60s_run_once()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        await SeedFeatureAsync(ctx, WorkspaceFeatureLifecycleState.Ready, WorkspaceFeatureRepositoryState.Ready);
        StubHealthyList(ctx);

        var reconciler = (WorkspaceFeatureReconciler)ctx.Resolve<IWorkspaceFeatureReconciler>();
        await reconciler.ReconcileAsync();
        await reconciler.ReconcileAsync();

        Assert.Equal(1, ctx.AgentBridge.Calls.Count(c => c.Command == AgentHubMethods.ListGitWorktrees));
    }

    [Fact]
    public async Task Special_Workspace_context_rows_are_unchanged_after_reconcile()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        var special = await ctx.GetSpecialContextIdAsync();
        await using (var scope = ctx.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var state = await db.WorkspaceRepositoryContextStates.SingleAsync(s =>
                s.WorkspaceFeatureContextId == special.Value
                && s.WorkspaceRepositoryId == ctx.WorkspaceRepositoryId);
            state.BranchName = "main";
            state.GitVersion = "1.2.3";
            state.OutgoingCommits = 1;
            state.IncomingCommits = 2;
            state.SyncStatus = RepoSyncStatus.InSync;
            await db.SaveChangesAsync();
        }

        await SeedFeatureAsync(ctx, WorkspaceFeatureLifecycleState.Ready, WorkspaceFeatureRepositoryState.Ready);
        StubHealthyList(ctx);

        var before = await SnapshotSpecialAsync(ctx, special);
        await ReconcileAsync(ctx);
        var after = await SnapshotSpecialAsync(ctx, special);

        Assert.Equal(before, after);
    }

    [Theory]
    [InlineData(@"C:\a\b\c", @"C:\a\b", true)]
    [InlineData(@"C:/a/b/c/", @"C:\a\b", true)]
    [InlineData(@"C:\a\b", @"C:\a\b", true)]
    [InlineData(@"C:\a\be", @"C:\a\b", false)]
    [InlineData(@"C:\other", @"C:\a\b", false)]
    public void IsPathUnder_normalizes_slashes_and_trailing_separators(string path, string root, bool expected)
    {
        Assert.Equal(expected, WorkspaceFeatureReconciler.IsPathUnder(path, root));
    }

    private static async Task ReconcileAsync(SyncStateTestContext ctx)
    {
        var reconciler = (WorkspaceFeatureReconciler)ctx.Resolve<IWorkspaceFeatureReconciler>();
        reconciler.ReconcileMinInterval = TimeSpan.Zero;
        await reconciler.ReconcileAsync();
    }

    private static void StubHealthyList(SyncStateTestContext ctx)
    {
        ctx.AgentBridge.Respond(AgentHubMethods.ListGitWorktrees, new
        {
            worktrees = new[]
            {
                new { worktreePath = WorktreePath, branchName = FeatureName, isBare = false },
            },
        });
    }

    private static async Task SeedFeatureAsync(
        SyncStateTestContext ctx,
        WorkspaceFeatureLifecycleState lifecycle,
        WorkspaceFeatureRepositoryState repoState)
    {
        await using var scope = ctx.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var workspace = await db.Workspaces.FirstAsync(w => w.WorkspaceId == ctx.WorkspaceId);
        workspace.ManagedFeatureStorageRoot = StorageRoot;

        var feature = new WorkspaceFeature
        {
            WorkspaceId = ctx.WorkspaceId,
            Name = FeatureName,
            LifecycleState = lifecycle,
            BaseKind = WorkspaceFeatureBaseKind.CurrentWorkspace,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        db.WorkspaceFeatures.Add(feature);
        await db.SaveChangesAsync();

        var context = new WorkspaceFeatureContext
        {
            WorkspaceId = ctx.WorkspaceId,
            Kind = WorkspaceFeatureContextKind.Feature,
            WorkspaceFeatureId = feature.WorkspaceFeatureId,
            CreatedAt = DateTime.UtcNow,
            IsInSync = true,
        };
        db.WorkspaceFeatureContexts.Add(context);
        await db.SaveChangesAsync();

        db.WorkspaceFeatureRepositories.Add(new WorkspaceFeatureRepository
        {
            WorkspaceFeatureContextId = context.WorkspaceFeatureContextId,
            WorkspaceRepositoryId = ctx.WorkspaceRepositoryId,
            WorktreePath = WorktreePath,
            State = repoState,
            BaseCommitSha = "abc123",
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private static async Task<WorkspaceFeature> ReadFeatureAsync(SyncStateTestContext ctx)
    {
        await using var scope = ctx.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.WorkspaceFeatures.AsNoTracking()
            .SingleAsync(f => f.WorkspaceId == ctx.WorkspaceId && f.Name == FeatureName);
    }

    private static async Task<WorkspaceFeatureRepository> ReadRepoRowAsync(SyncStateTestContext ctx)
    {
        await using var scope = ctx.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.WorkspaceFeatureRepositories.AsNoTracking()
            .SingleAsync(r => r.WorkspaceRepositoryId == ctx.WorkspaceRepositoryId);
    }

    private static async Task<string> SnapshotSpecialAsync(SyncStateTestContext ctx, WorkspaceFeatureContextId special)
    {
        await using var scope = ctx.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var context = await db.WorkspaceFeatureContexts.AsNoTracking()
            .SingleAsync(c => c.WorkspaceFeatureContextId == special.Value);
        var states = await db.WorkspaceRepositoryContextStates.AsNoTracking()
            .Where(s => s.WorkspaceFeatureContextId == special.Value)
            .OrderBy(s => s.WorkspaceRepositoryId)
            .Select(s => $"{s.WorkspaceRepositoryId}|{s.BranchName}|{s.GitVersion}|{s.OutgoingCommits}|{s.IncomingCommits}|{(int)s.SyncStatus}")
            .ToListAsync();
        return $"{context.WorkspaceFeatureContextId}|{(int)context.Kind}|{context.IsInSync}|{string.Join(";", states)}";
    }
}
