using GrayMoon.Abstractions.Worker;
using GrayMoon.App.Data;
using GrayMoon.App.Models;
using GrayMoon.App.Services.Features;
using GrayMoon.Application.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GrayMoon.App.Tests;

/// <summary>C4: Repair (retry) and Roll back Feature service operations.</summary>
public sealed class WorkspaceFeatureRepairTests
{
    private const string FeatureName = "feat-refresh";
    private const string ApiWorktreePath = @"C:\gm-test-root\.graymoon\test-ws\features\feat-refresh\graymoon-api";
    private const string WebWorktreePath = @"C:\gm-test-root\.graymoon\test-ws\features\feat-refresh\graymoon-web";

    [Fact]
    public async Task Repair_two_repos_fail_then_succeed_sets_Feature_Ready_and_seeds_projections()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        var featureContextId = await SeedBrokenFeatureWithTwoReposAsync(ctx);

        var firstRepairPass = true;
        ctx.WorkerBridge.Respond(WorkerHubMethods.CreateGitWorktree, args =>
        {
            if (firstRepairPass)
                return new WorkerCommandResponse(false, null, "simulated create failure");
            var path = GetWorktreePath(args);
            var wtPath = path.Contains("graymoon-web", StringComparison.OrdinalIgnoreCase)
                ? WebWorktreePath
                : ApiWorktreePath;
            return new WorkerCommandResponse(true, new { success = true, worktreePath = wtPath }, null);
        });

        await using var scope = ctx.CreateScope();
        var ops = scope.ServiceProvider.GetRequiredService<IWorkspaceFeatureOperations>();

        var first = await ops.RepairFeatureAsync(featureContextId);
        Assert.False(first.Success);
        Assert.Equal(2, first.Repositories.Count(r => r.Outcome == FeatureRepositoryOperationOutcome.Failed));

        firstRepairPass = false;
        ctx.WorkerBridge.Respond(WorkerHubMethods.CreateGitWorktree, args =>
        {
            var path = GetWorktreePath(args);
            var wtPath = path.Contains("graymoon-web", StringComparison.OrdinalIgnoreCase)
                ? WebWorktreePath
                : ApiWorktreePath;
            return new WorkerCommandResponse(true, new { success = true, worktreePath = wtPath }, null);
        });

        var second = await ops.RepairFeatureAsync(featureContextId);
        Assert.True(second.Success, second.Error);
        Assert.All(second.Repositories, r => Assert.Equal(FeatureRepositoryOperationOutcome.Succeeded, r.Outcome));

        await using var read = ctx.CreateScope();
        var db = read.ServiceProvider.GetRequiredService<AppDbContext>();
        var feature = await db.WorkspaceFeatures.SingleAsync(f => f.Name == FeatureName);
        Assert.Equal(WorkspaceFeatureLifecycleState.Ready, feature.LifecycleState);
        Assert.True(await db.WorkspaceRepositoryContextStates.AnyAsync(
            s => s.WorkspaceFeatureContextId == featureContextId.Value));
        Assert.Equal(4, ctx.WorkerBridge.Calls.Count(c => c.Command == WorkerHubMethods.CreateGitWorktree));
    }

    [Fact]
    public async Task Repair_all_repos_Ready_but_Feature_NeedsRepair_seeds_once_with_zero_CreateGitWorktree()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        var featureContextId = await SeedRemovableFeatureWithTwoReposAsync(ctx);
        await SetFeatureLifecycleAsync(ctx, featureContextId, WorkspaceFeatureLifecycleState.NeedsRepair);

        await using var scope = ctx.CreateScope();
        var ops = scope.ServiceProvider.GetRequiredService<IWorkspaceFeatureOperations>();
        var result = await ops.RepairFeatureAsync(featureContextId);

        Assert.True(result.Success, result.Error);
        Assert.DoesNotContain(
            ctx.WorkerBridge.Calls,
            c => c.Command == WorkerHubMethods.CreateGitWorktree);
        Assert.All(result.Repositories, r => Assert.Equal(FeatureRepositoryOperationOutcome.Skipped, r.Outcome));

        await using var read = ctx.CreateScope();
        var db = read.ServiceProvider.GetRequiredService<AppDbContext>();
        var feature = await db.WorkspaceFeatures.SingleAsync(f => f.Name == FeatureName);
        Assert.Equal(WorkspaceFeatureLifecycleState.Ready, feature.LifecycleState);
        Assert.True(await db.WorkspaceRepositoryContextStates.AnyAsync(
            s => s.WorkspaceFeatureContextId == featureContextId.Value));
    }

    [Fact]
    public async Task GetFeatureStatus_lists_every_repo_with_state_and_error()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        var featureContextId = await SeedBrokenFeatureWithTwoReposAsync(ctx);

        await using var scope = ctx.CreateScope();
        var ops = scope.ServiceProvider.GetRequiredService<IWorkspaceFeatureOperations>();
        var status = await ops.GetFeatureStatusAsync(featureContextId);

        Assert.NotNull(status);
        Assert.Equal(FeatureName, status.FeatureName);
        Assert.False(status.IsRemoveIncomplete);
        Assert.Equal(2, status.Repositories.Count);
        Assert.DoesNotContain(status.Repositories, r => string.IsNullOrWhiteSpace(r.RepositoryName));
    }

    [Fact]
    public async Task Repair_one_repo_still_failing_keeps_Feature_NeedsRepair_with_repo_message()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        var featureContextId = await SeedBrokenFeatureWithTwoReposAsync(ctx);

        ctx.WorkerBridge.Respond(WorkerHubMethods.CreateGitWorktree, args =>
        {
            var path = GetWorktreePath(args);
            if (path.Contains("graymoon-web", StringComparison.OrdinalIgnoreCase))
                return new WorkerCommandResponse(false, null, "web worktree failed");
            return new WorkerCommandResponse(true, new { success = true, worktreePath = ApiWorktreePath }, null);
        });

        await using var scope = ctx.CreateScope();
        var ops = scope.ServiceProvider.GetRequiredService<IWorkspaceFeatureOperations>();
        var result = await ops.RepairFeatureAsync(featureContextId);

        Assert.False(result.Success);
        var failed = Assert.Single(result.Repositories, r => r.Outcome == FeatureRepositoryOperationOutcome.Failed);
        Assert.Contains("web worktree failed", failed.Message);

        await using var read = ctx.CreateScope();
        var db = read.ServiceProvider.GetRequiredService<AppDbContext>();
        var feature = await db.WorkspaceFeatures.SingleAsync(f => f.Name == FeatureName);
        Assert.Equal(WorkspaceFeatureLifecycleState.NeedsRepair, feature.LifecycleState);
    }

    [Fact]
    public async Task Rollback_removes_registered_worktrees_and_deletes_Feature_rows()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        var featureContextId = await SeedRemovableFeatureWithTwoReposAsync(ctx);

        ctx.WorkerBridge.Respond(WorkerHubMethods.InspectWorktree, CleanInspectWorktree());
        ctx.WorkerBridge.Respond(WorkerHubMethods.ListGitWorktrees, args =>
        {
            var main = GetMainRepositoryPath(args);
            return new WorkerCommandResponse(true, new
            {
                worktrees = new object[]
                {
                    new { worktreePath = main, branchName = "feature/x", isBare = false },
                    new { worktreePath = ApiWorktreePath, branchName = FeatureName, isBare = false },
                    new { worktreePath = WebWorktreePath, branchName = FeatureName, isBare = false },
                },
            }, null);
        });
        ctx.WorkerBridge.Respond(WorkerHubMethods.RemoveGitWorktree, new { success = true });
        ctx.WorkerBridge.Respond("DeleteBranch", new { success = true });

        await using var scope = ctx.CreateScope();
        var ops = scope.ServiceProvider.GetRequiredService<IWorkspaceFeatureOperations>();
        var result = await ops.RollbackFeatureAsync(featureContextId);

        Assert.True(result.Success, result.Error);
        Assert.Equal(2, ctx.WorkerBridge.Calls.Count(c => c.Command == WorkerHubMethods.RemoveGitWorktree));
        Assert.Equal(2, ctx.WorkerBridge.Calls.Count(c => c.Command == "DeleteBranch"));

        await using var read = ctx.CreateScope();
        var db = read.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await db.WorkspaceFeatureContexts.AnyAsync(c => c.WorkspaceFeatureContextId == featureContextId.Value));
        Assert.False(await db.WorkspaceFeatureRepositories.AnyAsync(r => r.WorkspaceFeatureContextId == featureContextId.Value));
        Assert.False(await db.WorkspaceFeatures.AnyAsync(f => f.Name == FeatureName));
    }

    [Fact]
    public async Task Rollback_refuses_when_a_repo_is_dirty()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        var featureContextId = await SeedRemovableFeatureWithTwoReposAsync(ctx);

        ctx.WorkerBridge.Respond(WorkerHubMethods.InspectWorktree, new
        {
            exists = true,
            isDirty = true,
            branch = FeatureName,
        });

        await using var scope = ctx.CreateScope();
        var ops = scope.ServiceProvider.GetRequiredService<IWorkspaceFeatureOperations>();
        var result = await ops.RollbackFeatureAsync(featureContextId);

        Assert.False(result.Success);
        Assert.Contains("uncommitted", result.Error ?? "", StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(ctx.WorkerBridge.Calls, c => c.Command == WorkerHubMethods.RemoveGitWorktree);

        await using var read = ctx.CreateScope();
        var db = read.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.True(await db.WorkspaceFeatures.AnyAsync(f => f.Name == FeatureName));
    }

    [Fact]
    public async Task Remove_incomplete_refuses_repair_and_rollback_with_zero_CreateGitWorktree()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        var featureContextId = await SeedRemovableFeatureWithTwoReposAsync(ctx);

        await using (var seedScope = ctx.CreateScope())
        {
            var db = seedScope.ServiceProvider.GetRequiredService<AppDbContext>();
            var rows = await db.WorkspaceFeatureRepositories
                .Where(r => r.WorkspaceFeatureContextId == featureContextId.Value)
                .OrderBy(r => r.WorkspaceFeatureRepositoryId)
                .ToListAsync();
            rows[0].State = WorkspaceFeatureRepositoryState.Removed;
            rows[1].State = WorkspaceFeatureRepositoryState.Removing;
            rows[1].LastError = "Permission denied";
            await db.SaveChangesAsync();
        }

        await using var scope = ctx.CreateScope();
        var ops = scope.ServiceProvider.GetRequiredService<IWorkspaceFeatureOperations>();
        Assert.True(await ops.IsRemoveIncompleteAsync(featureContextId));

        var repair = await ops.RepairFeatureAsync(featureContextId);
        Assert.False(repair.Success);
        Assert.Contains("Continue removal", repair.Error);

        var rollback = await ops.RollbackFeatureAsync(featureContextId);
        Assert.False(rollback.Success);
        Assert.Contains("Continue removal", rollback.Error);

        Assert.DoesNotContain(ctx.WorkerBridge.Calls, c => c.Command == WorkerHubMethods.CreateGitWorktree);
    }

    [Fact]
    public async Task Rollback_never_deletes_default_branch_or_primary_checkout_branch()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        var featureContextId = await SeedRemovableFeatureAsync(ctx, featureName: "main");
        const string mainFeatureWorktree = @"C:\gm-test-root\.graymoon\test-ws\features\main\graymoon-api";

        ctx.WorkerBridge.Respond(WorkerHubMethods.InspectWorktree, CleanInspectWorktree());
        ctx.WorkerBridge.Respond(WorkerHubMethods.ListGitWorktrees, args =>
        {
            var main = GetMainRepositoryPath(args);
            return new WorkerCommandResponse(true, new
            {
                worktrees = new object[]
                {
                    new { worktreePath = main, branchName = "main", isBare = false },
                    new { worktreePath = mainFeatureWorktree, branchName = "main", isBare = false },
                },
            }, null);
        });
        ctx.WorkerBridge.Respond(WorkerHubMethods.RemoveGitWorktree, new { success = true });
        ctx.WorkerBridge.Respond("DeleteBranch", new { success = true });

        await using var scope = ctx.CreateScope();
        var ops = scope.ServiceProvider.GetRequiredService<IWorkspaceFeatureOperations>();
        var result = await ops.RollbackFeatureAsync(featureContextId);

        Assert.True(result.Success, result.Error);
        Assert.DoesNotContain(ctx.WorkerBridge.Calls, c => c.Command == "DeleteBranch");
        var repoResult = Assert.Single(result.Repositories);
        Assert.Equal(FeatureRepositoryOperationOutcome.Skipped, repoResult.Outcome);
    }

    private static object CleanInspectWorktree() => new
    {
        exists = true,
        isDirty = false,
        hasUpstream = true,
        aheadOfUpstream = 0,
        aheadOfDefault = 0,
        branch = FeatureName,
    };

    private static string GetWorktreePath(object args)
    {
        var prop = args.GetType().GetProperty("worktreePath");
        return (prop?.GetValue(args) as string) ?? "";
    }

    private static string GetMainRepositoryPath(object args)
    {
        var prop = args.GetType().GetProperty("mainRepositoryPath");
        return (prop?.GetValue(args) as string) ?? @"C:\gm-test-root\test-ws\graymoon-api";
    }

    private static async Task SetFeatureLifecycleAsync(
        SyncStateTestContext ctx,
        WorkspaceFeatureContextId featureContextId,
        WorkspaceFeatureLifecycleState state)
    {
        await using var scope = ctx.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var feature = await db.WorkspaceFeatures.SingleAsync(f =>
            f.WorkspaceId == ctx.WorkspaceId && f.Name == FeatureName);
        feature.LifecycleState = state;
        feature.LastError = "needs attention";
        await db.SaveChangesAsync();
    }

    private static async Task<WorkspaceFeatureContextId> SeedBrokenFeatureWithTwoReposAsync(SyncStateTestContext ctx)
    {
        var featureContextId = await SeedRemovableFeatureWithTwoReposAsync(ctx);
        await using var scope = ctx.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var feature = await db.WorkspaceFeatures.SingleAsync(f => f.Name == FeatureName);
        feature.LifecycleState = WorkspaceFeatureLifecycleState.NeedsRepair;
        feature.LastError = "One or more worktrees failed to create.";
        var rows = await db.WorkspaceFeatureRepositories
            .Where(r => r.WorkspaceFeatureContextId == featureContextId.Value)
            .ToListAsync();
        foreach (var row in rows)
        {
            row.State = WorkspaceFeatureRepositoryState.NeedsRepair;
            row.LastError = "CreateGitWorktree failed.";
        }
        await db.SaveChangesAsync();
        return featureContextId;
    }

    private static async Task<WorkspaceFeatureContextId> SeedRemovableFeatureAsync(
        SyncStateTestContext ctx,
        string featureName = FeatureName)
    {
        await using var scope = ctx.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var feature = new WorkspaceFeature
        {
            WorkspaceId = ctx.WorkspaceId,
            Name = featureName,
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
            WorktreePath = $@"C:\gm-test-root\.graymoon\test-ws\features\{featureName}\graymoon-api",
            State = WorkspaceFeatureRepositoryState.Ready,
            BaseCommitSha = "abc123",
            ParentBranchName = "feature/x",
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        return new WorkspaceFeatureContextId(context.WorkspaceFeatureContextId);
    }

    private static async Task<WorkspaceFeatureContextId> SeedRemovableFeatureWithTwoReposAsync(SyncStateTestContext ctx)
    {
        var featureContextId = await SeedRemovableFeatureAsync(ctx);
        await AddExtraRepositoryAsync(ctx, featureContextId, "graymoon-web");
        return featureContextId;
    }

    private static async Task AddExtraRepositoryAsync(
        SyncStateTestContext ctx,
        WorkspaceFeatureContextId featureContextId,
        string repositoryName)
    {
        await using var scope = ctx.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var connectorId = await db.Repositories
            .Where(r => r.RepositoryId == ctx.RepositoryId)
            .Select(r => r.ConnectorId)
            .SingleAsync();

        var repo = new Repository
        {
            ConnectorId = connectorId,
            RepositoryName = repositoryName,
            OrgName = "acme",
            Visibility = "Public",
            CloneUrl = $"https://github.com/acme/{repositoryName}.git",
        };
        db.Repositories.Add(repo);
        await db.SaveChangesAsync();

        var link = new WorkspaceRepositoryLink
        {
            WorkspaceId = ctx.WorkspaceId,
            RepositoryId = repo.RepositoryId,
            GitVersion = "1.0.0",
            BranchName = "main",
            DefaultBranchName = "main",
            OutgoingCommits = 0,
            IncomingCommits = 0,
            BranchHasUpstream = true,
            SyncStatus = RepoSyncStatus.InSync,
            RepositoryType = ProjectType.Library,
        };
        db.WorkspaceRepositories.Add(link);
        await db.SaveChangesAsync();

        db.WorkspaceFeatureRepositories.Add(new WorkspaceFeatureRepository
        {
            WorkspaceFeatureContextId = featureContextId.Value,
            WorkspaceRepositoryId = link.WorkspaceRepositoryId,
            WorktreePath = $@"C:\gm-test-root\.graymoon\test-ws\features\{FeatureName}\{repositoryName}",
            State = WorkspaceFeatureRepositoryState.Ready,
            BaseCommitSha = "def456",
            ParentBranchName = "main",
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }
}
