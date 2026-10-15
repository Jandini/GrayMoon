using GrayMoon.Abstractions.Worker;
using GrayMoon.App.Data;
using GrayMoon.App.Models;
using GrayMoon.App.Services.Workspaces;
using GrayMoon.Application;
using GrayMoon.Application.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GrayMoon.App.Tests;

/// <summary>
/// RepositoryBranches must follow git: Create Feature adds the Feature branch, and Remove Feature drops it
/// only when git confirmed the delete. A kept branch keeps its row.
/// </summary>
public sealed class FeatureBranchPersistenceTests
{
    private const string FeatureName = "feat-refresh";

    private static object CleanInspectWorktree() => new
    {
        exists = true,
        isDirty = false,
        hasUpstream = false,
        aheadOfUpstream = 0,
        aheadOfDefault = 0,
        branch = FeatureName,
        featureBranchExists = true,
        featureBranchAheadOfDefault = 0,
        featureBranchHasUpstream = false,
        featureBranchAheadOfUpstream = (int?)null,
    };

    [Fact]
    public async Task Remove_Feature_drops_the_local_branch_row_after_git_deleted_it()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        var featureContextId = await SeedRemovableFeatureAsync(ctx);
        await SeedBranchRowsAsync(ctx, (FeatureName, false), ("main", false), ($"origin/{FeatureName}", true));

        ctx.WorkerBridge.Respond(WorkerHubMethods.InspectWorktree, CleanInspectWorktree());
        ctx.WorkerBridge.Respond(WorkerHubMethods.RemoveGitWorktree, new { success = true });
        ctx.WorkerBridge.Respond("DeleteBranch", new { success = true });

        var result = await RemoveAsync(ctx, featureContextId, new RemoveFeatureOptions
        {
            AllowDiscardUncommitted = true,
            DeleteLocalBranches = true,
        });

        Assert.True(result.Success, result.Error);
        Assert.Equal(RemoveFeatureBranchOutcome.Deleted, Assert.Single(result.RemoveFeatureReport!).BranchOutcome);

        var rows = await ReadBranchRowsAsync(ctx);
        Assert.DoesNotContain((FeatureName, false), rows);
        Assert.Contains(("main", false), rows);
        // The remote ref was not deleted, so its row stays.
        Assert.Contains(($"origin/{FeatureName}", true), rows);
    }

    [Fact]
    public async Task Remove_Feature_keeps_the_branch_row_when_git_refused_the_delete()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        var featureContextId = await SeedRemovableFeatureAsync(ctx);
        await SeedBranchRowsAsync(ctx, (FeatureName, false), ("main", false));

        ctx.WorkerBridge.Respond(WorkerHubMethods.InspectWorktree, CleanInspectWorktree());
        ctx.WorkerBridge.Respond(WorkerHubMethods.RemoveGitWorktree, new { success = true });
        ctx.WorkerBridge.Respond(
            "DeleteBranch",
            data: null,
            success: false,
            error: $"error: The branch '{FeatureName}' is not fully merged.");

        var result = await RemoveAsync(ctx, featureContextId, new RemoveFeatureOptions
        {
            AllowDiscardUncommitted = true,
            DeleteLocalBranches = true,
        });

        Assert.True(result.Success, result.Error);
        Assert.Equal(RemoveFeatureBranchOutcome.KeptUnmerged, Assert.Single(result.RemoveFeatureReport!).BranchOutcome);
        Assert.Contains((FeatureName, false), await ReadBranchRowsAsync(ctx));
    }

    [Fact]
    public async Task Remove_Feature_keeps_the_branch_row_when_branch_delete_was_not_selected()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        var featureContextId = await SeedRemovableFeatureAsync(ctx);
        await SeedBranchRowsAsync(ctx, (FeatureName, false));

        ctx.WorkerBridge.Respond(WorkerHubMethods.InspectWorktree, CleanInspectWorktree());
        ctx.WorkerBridge.Respond(WorkerHubMethods.RemoveGitWorktree, new { success = true });

        var result = await RemoveAsync(ctx, featureContextId, new RemoveFeatureOptions
        {
            AllowDiscardUncommitted = true,
            DeleteLocalBranches = false,
        });

        Assert.True(result.Success, result.Error);
        Assert.DoesNotContain(ctx.WorkerBridge.Calls, c => c.Command == "DeleteBranch");
        Assert.Contains((FeatureName, false), await ReadBranchRowsAsync(ctx));
    }

    [Fact]
    public async Task Create_Feature_persists_the_Feature_branch_once()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        await SeedBranchRowsAsync(ctx, ("main", false));

        ctx.WorkerBridge.Respond(WorkerHubMethods.GetHeadCommits, new
        {
            commits = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["graymoon-api"] = "abc123def456abc123def456abc123def456abc1",
            },
            branches = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["graymoon-api"] = "main",
            },
        });
        ctx.WorkerBridge.Respond(WorkerHubMethods.CreateGitWorktree, new { success = true, worktreePath = @"C:\wt" });

        await using var scope = ctx.CreateScope();
        var ops = scope.ServiceProvider.GetRequiredService<IWorkspaceFeatureOperations>();
        var result = await ops.CreateFeatureAsync(ctx.WorkspaceId, "feature/payments", WorkspaceFeatureBaseKindApplication.CurrentWorkspace);

        Assert.True(result.Success, result.Error);
        var rows = await ReadBranchRowsAsync(ctx);
        Assert.Equal(1, rows.Count(r => r == ("feature/payments", false)));
        Assert.Contains(("main", false), rows);
    }

    [Fact]
    public async Task Create_Feature_does_not_persist_a_branch_when_the_worktree_failed()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();

        ctx.WorkerBridge.Respond(WorkerHubMethods.GetHeadCommits, new
        {
            commits = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["graymoon-api"] = "abc123def456abc123def456abc123def456abc1",
            },
            branches = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["graymoon-api"] = "main",
            },
        });
        ctx.WorkerBridge.Respond(WorkerHubMethods.CreateGitWorktree, new { success = false, errorMessage = "boom" });

        await using var scope = ctx.CreateScope();
        var ops = scope.ServiceProvider.GetRequiredService<IWorkspaceFeatureOperations>();
        var result = await ops.CreateFeatureAsync(ctx.WorkspaceId, "feature/payments", WorkspaceFeatureBaseKindApplication.CurrentWorkspace);

        Assert.False(result.Success);
        Assert.DoesNotContain(("feature/payments", false), await ReadBranchRowsAsync(ctx));
    }

    [Fact]
    public async Task Writer_removes_a_remote_branch_by_bare_or_prefixed_name_and_leaves_the_local_branch()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        await SeedBranchRowsAsync(ctx, ("topic", false), ("origin/topic", true), ("origin/other", true));

        await using var scope = ctx.CreateScope();
        var writer = scope.ServiceProvider.GetRequiredService<RepositoryBranchWriter>();

        Assert.Equal(1, await writer.RemoveBranchAsync(ctx.WorkspaceRepositoryId, "topic", isRemote: true));
        Assert.Equal(0, await writer.RemoveBranchAsync(ctx.WorkspaceRepositoryId, "origin/topic", isRemote: true));

        var rows = await ReadBranchRowsAsync(ctx);
        Assert.Contains(("topic", false), rows);
        Assert.Contains(("origin/other", true), rows);
        Assert.DoesNotContain(("origin/topic", true), rows);

        Assert.Equal(1, await writer.RemoveBranchAsync(ctx.WorkspaceRepositoryId, "origin/other", isRemote: true));
        Assert.Equal(1, await writer.RemoveBranchAsync(ctx.WorkspaceRepositoryId, "topic", isRemote: false));
        Assert.Empty(await ReadBranchRowsAsync(ctx));
    }

    [Fact]
    public async Task Writer_never_removes_a_tag_that_shares_the_branch_name()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        await using (var scope = ctx.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.RepositoryBranches.Add(new RepositoryBranch
            {
                WorkspaceRepositoryId = ctx.WorkspaceRepositoryId,
                BranchName = "v1",
                IsRemote = false,
                IsTag = true,
                LastSeenAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        await using var writerScope = ctx.CreateScope();
        var writer = writerScope.ServiceProvider.GetRequiredService<RepositoryBranchWriter>();
        Assert.Equal(0, await writer.RemoveBranchAsync(ctx.WorkspaceRepositoryId, "v1", isRemote: false));

        await using var read = ctx.CreateScope();
        var readDb = read.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.True(await readDb.RepositoryBranches.AnyAsync(rb => rb.BranchName == "v1" && rb.IsTag));
    }

    private static async Task<OperationResult> RemoveAsync(
        SyncStateTestContext ctx,
        WorkspaceFeatureContextId featureContextId,
        RemoveFeatureOptions options)
    {
        await using var scope = ctx.CreateScope();
        var ops = scope.ServiceProvider.GetRequiredService<IWorkspaceFeatureOperations>();
        return await ops.RemoveFeatureAsync(featureContextId, options);
    }

    private static async Task SeedBranchRowsAsync(SyncStateTestContext ctx, params (string Name, bool IsRemote)[] branches)
    {
        await using var scope = ctx.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        foreach (var (name, isRemote) in branches)
        {
            db.RepositoryBranches.Add(new RepositoryBranch
            {
                WorkspaceRepositoryId = ctx.WorkspaceRepositoryId,
                BranchName = name,
                IsRemote = isRemote,
                LastSeenAt = DateTime.UtcNow,
            });
        }

        await db.SaveChangesAsync();
    }

    private static async Task<List<(string Name, bool IsRemote)>> ReadBranchRowsAsync(SyncStateTestContext ctx)
    {
        await using var scope = ctx.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var rows = await db.RepositoryBranches
            .AsNoTracking()
            .Where(rb => rb.WorkspaceRepositoryId == ctx.WorkspaceRepositoryId && !rb.IsTag)
            .Select(rb => new { rb.BranchName, rb.IsRemote })
            .ToListAsync();
        return rows.Select(r => (r.BranchName, r.IsRemote)).ToList();
    }

    private static async Task<WorkspaceFeatureContextId> SeedRemovableFeatureAsync(SyncStateTestContext ctx)
    {
        await using var scope = ctx.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var feature = new WorkspaceFeature
        {
            WorkspaceId = ctx.WorkspaceId,
            Name = FeatureName,
            LifecycleState = WorkspaceFeatureLifecycleState.Ready,
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
            WorktreePath = $@"C:\gm-test-root\.graymoon\test-ws\features\{FeatureName}\graymoon-api",
            State = WorkspaceFeatureRepositoryState.Ready,
            BaseCommitSha = "abc123",
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        return new WorkspaceFeatureContextId(context.WorkspaceFeatureContextId);
    }
}
