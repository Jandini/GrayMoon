using GrayMoon.Abstractions.Agent;
using GrayMoon.App.Data;
using GrayMoon.App.Models;
using GrayMoon.App.Services.GitChanges;
using GrayMoon.Application;
using GrayMoon.Application.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GrayMoon.App.Tests;

/// <summary>
/// D2: Remove Feature must report exactly what was removed and what was not, per repository, and
/// must never let a local branch delete failure only reach the log. Also covers the new `Removing`
/// / `Removed` row states and the Git Changes monitoring pause/resume around a Remove.
/// </summary>
public sealed class RemoveFeatureReportTests
{
    private static object CleanInspectWorktree() => new
    {
        exists = true,
        isDirty = false,
        hasUpstream = true,
        aheadOfUpstream = 0,
        aheadOfDefault = 0,
    };

    [Fact]
    public async Task Branch_delete_failure_without_force_is_reported_as_kept_not_only_logged()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        var featureContextId = await SeedRemovableFeatureAsync(ctx);

        ctx.AgentBridge.Respond(AgentHubMethods.InspectWorktree, CleanInspectWorktree());
        ctx.AgentBridge.Respond(AgentHubMethods.RemoveGitWorktree, new { success = true });
        ctx.AgentBridge.Respond(
            "DeleteBranch",
            data: null,
            success: false,
            error: "error: The branch 'feat-refresh' is not fully merged.");

        await using var scope = ctx.CreateScope();
        var ops = scope.ServiceProvider.GetRequiredService<IWorkspaceFeatureOperations>();
        var result = await ops.RemoveFeatureAsync(
            featureContextId,
            new RemoveFeatureOptions { AllowDiscardUncommitted = true });

        Assert.True(result.Success, result.Error);
        Assert.NotNull(result.RemoveFeatureReport);
        var repo = Assert.Single(result.RemoveFeatureReport!);
        Assert.True(repo.WorktreeRemoved);
        Assert.Equal(RemoveFeatureBranchOutcome.KeptUnmerged, repo.BranchOutcome);
        Assert.Contains("not fully merged", repo.BranchMessage);

        await using var read = ctx.CreateScope();
        var db = read.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await db.WorkspaceFeatureContexts.AnyAsync(c => c.WorkspaceFeatureContextId == featureContextId.Value));
    }

    [Fact]
    public async Task Residue_reported_by_Agent_appears_in_the_report()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        var featureContextId = await SeedRemovableFeatureAsync(ctx);

        ctx.AgentBridge.Respond(AgentHubMethods.InspectWorktree, CleanInspectWorktree());
        ctx.AgentBridge.Respond(AgentHubMethods.RemoveGitWorktree, new
        {
            success = true,
            residueRemaining = true,
            residueFileCount = 2,
            residueSampleFiles = new[] { "build.log" },
            residueMessage = "Some files could not be deleted. They may still be open in another program.",
        });
        ctx.AgentBridge.Respond("DeleteBranch", new { success = true });

        await using var scope = ctx.CreateScope();
        var ops = scope.ServiceProvider.GetRequiredService<IWorkspaceFeatureOperations>();
        var result = await ops.RemoveFeatureAsync(
            featureContextId,
            new RemoveFeatureOptions { AllowDiscardUncommitted = true, AllowForceDeleteLocalBranches = true });

        Assert.True(result.Success, result.Error);
        var repo = Assert.Single(result.RemoveFeatureReport!);
        Assert.Equal(RemoveFeatureBranchOutcome.Deleted, repo.BranchOutcome);
        Assert.True(repo.ResidueRemaining);
        Assert.Equal(2, repo.ResidueFileCount);
        Assert.Contains("build.log", repo.ResidueSampleFiles!);
        Assert.Contains("could not be deleted", repo.ResidueMessage);
    }

    [Fact]
    public async Task One_of_three_repos_failing_leaves_the_other_two_Removed_and_the_Feature_NeedsRepair()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        var featureContextId = await SeedRemovableFeatureWithThreeReposAsync(ctx);

        ctx.AgentBridge.Respond(AgentHubMethods.InspectWorktree, CleanInspectWorktree());
        ctx.AgentBridge.Respond(AgentHubMethods.RemoveGitWorktree, args =>
        {
            var path = GetWorktreePath(args);
            if (path.EndsWith("graymoon-web", StringComparison.Ordinal))
                return new AgentCommandResponse(false, null, "Permission denied on graymoon-web");
            return new AgentCommandResponse(true, new { success = true }, null);
        });
        ctx.AgentBridge.Respond("DeleteBranch", new { success = true });

        await using var scope = ctx.CreateScope();
        var ops = scope.ServiceProvider.GetRequiredService<IWorkspaceFeatureOperations>();
        var result = await ops.RemoveFeatureAsync(
            featureContextId,
            new RemoveFeatureOptions { AllowDiscardUncommitted = true, AllowForceDeleteLocalBranches = true });

        Assert.False(result.Success);
        Assert.Contains("Permission denied", result.Error);

        await using var read = ctx.CreateScope();
        var db = read.ServiceProvider.GetRequiredService<AppDbContext>();
        var feature = await db.WorkspaceFeatures.SingleAsync(f => f.Name == "feat-refresh" && f.WorkspaceId == ctx.WorkspaceId);
        Assert.Equal(WorkspaceFeatureLifecycleState.NeedsRepair, feature.LifecycleState);

        var rows = await db.WorkspaceFeatureRepositories
            .Where(r => r.WorkspaceFeatureContextId == featureContextId.Value)
            .ToListAsync();
        Assert.Equal(3, rows.Count);
        Assert.Equal(2, rows.Count(r => r.State == WorkspaceFeatureRepositoryState.Removed));
        var failedRow = Assert.Single(rows, r => r.State == WorkspaceFeatureRepositoryState.Removing);
        Assert.False(string.IsNullOrWhiteSpace(failedRow.LastError));
        Assert.Contains("Permission denied", failedRow.LastError);
    }

    [Fact]
    public async Task Second_Remove_skips_an_already_Removed_row_and_issues_zero_Agent_calls_for_it()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        var featureContextId = await SeedRemovableFeatureWithTwoReposAsync(ctx);

        string removedWorktreePath;
        await using (var scope = ctx.CreateScope())
        {
            var seedDb = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var rows = await seedDb.WorkspaceFeatureRepositories
                .Where(r => r.WorkspaceFeatureContextId == featureContextId.Value)
                .OrderBy(r => r.WorkspaceFeatureRepositoryId)
                .ToListAsync();
            var alreadyRemoved = rows[0];
            alreadyRemoved.State = WorkspaceFeatureRepositoryState.Removed;
            removedWorktreePath = alreadyRemoved.WorktreePath;
            await seedDb.SaveChangesAsync();
        }

        ctx.AgentBridge.Respond(AgentHubMethods.InspectWorktree, CleanInspectWorktree());
        ctx.AgentBridge.Respond(AgentHubMethods.RemoveGitWorktree, new { success = true });
        ctx.AgentBridge.Respond("DeleteBranch", new { success = true });

        await using var scope2 = ctx.CreateScope();
        var ops = scope2.ServiceProvider.GetRequiredService<IWorkspaceFeatureOperations>();

        var plan = await ops.AnalyzeRemoveFeatureAsync(featureContextId);
        Assert.True(plan.Success, plan.Error);
        Assert.Single(plan.Repositories);

        var result = await ops.RemoveFeatureAsync(
            featureContextId,
            new RemoveFeatureOptions { AllowDiscardUncommitted = true, AllowForceDeleteLocalBranches = true });

        Assert.True(result.Success, result.Error);
        Assert.Single(result.RemoveFeatureReport!);
        Assert.DoesNotContain(
            ctx.AgentBridge.Calls,
            c => c.Command == AgentHubMethods.RemoveGitWorktree && GetWorktreePath(c.Args) == removedWorktreePath);
        Assert.Equal(1, ctx.AgentBridge.Calls.Count(c => c.Command == AgentHubMethods.RemoveGitWorktree));
        Assert.Equal(1, ctx.AgentBridge.Calls.Count(c => c.Command == "DeleteBranch"));

        await using var read = ctx.CreateScope();
        var db = read.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await db.WorkspaceFeatureContexts.AnyAsync(c => c.WorkspaceFeatureContextId == featureContextId.Value));
        Assert.False(await db.WorkspaceFeatureRepositories.AnyAsync(r => r.WorkspaceFeatureContextId == featureContextId.Value));
    }

    [Fact]
    public async Task Workspace_monitoring_is_not_paused_while_a_Feature_is_removed()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        var featureContextId = await SeedRemovableFeatureAsync(ctx);
        var specialContextId = await ctx.GetSpecialContextIdAsync();

        ctx.AgentBridge.Respond(AgentHubMethods.InspectWorktree, CleanInspectWorktree());
        ctx.AgentBridge.Respond(AgentHubMethods.RemoveGitWorktree, new { success = true });
        ctx.AgentBridge.Respond("DeleteBranch", new { success = true });

        await using var scope = ctx.CreateScope();
        var ops = scope.ServiceProvider.GetRequiredService<IWorkspaceFeatureOperations>();
        var pause = scope.ServiceProvider.GetRequiredService<IWorkspaceGitChangesMonitoringPause>();

        var result = await ops.RemoveFeatureAsync(
            featureContextId,
            new RemoveFeatureOptions { AllowDiscardUncommitted = true, AllowForceDeleteLocalBranches = true });

        Assert.True(result.Success, result.Error);
        // The pause is released once Remove finishes; the special Workspace context is never paused.
        Assert.False(pause.IsPaused(featureContextId.Value));
        Assert.False(pause.IsPaused(specialContextId.Value));
    }

    [Fact]
    public async Task Locked_worktree_reported_by_InspectWorktree_appears_in_the_plan()
    {
        // D5: a locked worktree (git worktree lock) is explained in the plan rather than only
        // surfacing as a raw Git error from a later remove attempt.
        await using var ctx = await SyncStateTestContext.CreateAsync();
        var featureContextId = await SeedRemovableFeatureAsync(ctx);

        ctx.AgentBridge.Respond(AgentHubMethods.InspectWorktree, new
        {
            exists = true,
            isLocked = true,
            lockReason = "testing",
            isDirty = false,
            hasUpstream = true,
            aheadOfUpstream = 0,
            aheadOfDefault = 0,
        });

        await using var scope = ctx.CreateScope();
        var ops = scope.ServiceProvider.GetRequiredService<IWorkspaceFeatureOperations>();
        var plan = await ops.AnalyzeRemoveFeatureAsync(featureContextId);

        Assert.True(plan.Success, plan.Error);
        var repo = Assert.Single(plan.Repositories);
        Assert.True(repo.IsLocked);
        Assert.Equal("testing", repo.LockReason);
        Assert.False(plan.IsAutomaticallySafe);
    }

    [Fact]
    public async Task Old_worker_InspectWorktree_response_without_isLocked_field_is_not_locked()
    {
        // D5: an old Worker's response shape has no isLocked/lockReason fields at all; this must
        // deserialize to "not locked" rather than throwing or being treated as locked.
        await using var ctx = await SyncStateTestContext.CreateAsync();
        var featureContextId = await SeedRemovableFeatureAsync(ctx);

        ctx.AgentBridge.Respond(AgentHubMethods.InspectWorktree, CleanInspectWorktree());

        await using var scope = ctx.CreateScope();
        var ops = scope.ServiceProvider.GetRequiredService<IWorkspaceFeatureOperations>();
        var plan = await ops.AnalyzeRemoveFeatureAsync(featureContextId);

        Assert.True(plan.Success, plan.Error);
        var repo = Assert.Single(plan.Repositories);
        Assert.False(repo.IsLocked);
        Assert.Null(repo.LockReason);
    }

    [Fact]
    public async Task RemoveFeatureAsync_forwards_AllowUnlockWorktrees_as_unlock_on_the_RemoveGitWorktree_command()
    {
        // D5: the App never unlocks a worktree itself (it never touches the developer's disk); it
        // only forwards the user's consent to the Agent, which runs git worktree unlock.
        await using var ctx = await SyncStateTestContext.CreateAsync();
        var featureContextId = await SeedRemovableFeatureAsync(ctx);

        ctx.AgentBridge.Respond(AgentHubMethods.InspectWorktree, new
        {
            exists = true,
            isLocked = true,
            lockReason = "testing",
            isDirty = false,
            hasUpstream = true,
            aheadOfUpstream = 0,
            aheadOfDefault = 0,
        });
        ctx.AgentBridge.Respond(AgentHubMethods.RemoveGitWorktree, new { success = true });
        ctx.AgentBridge.Respond("DeleteBranch", new { success = true });

        await using var scope = ctx.CreateScope();
        var ops = scope.ServiceProvider.GetRequiredService<IWorkspaceFeatureOperations>();
        var result = await ops.RemoveFeatureAsync(
            featureContextId,
            new RemoveFeatureOptions { AllowUnlockWorktrees = true });

        Assert.True(result.Success, result.Error);
        var call = Assert.Single(ctx.AgentBridge.Calls, c => c.Command == AgentHubMethods.RemoveGitWorktree);
        var unlockProp = call.Args.GetType().GetProperty("unlock");
        Assert.NotNull(unlockProp);
        Assert.Equal(true, unlockProp!.GetValue(call.Args));
    }

    [Fact]
    public async Task Failed_remove_restarts_the_Feature_context_monitoring()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        var featureContextId = await SeedRemovableFeatureAsync(ctx);

        ctx.AgentBridge.Respond(AgentHubMethods.InspectWorktree, CleanInspectWorktree());
        ctx.AgentBridge.Respond(
            AgentHubMethods.RemoveGitWorktree,
            data: null,
            success: false,
            error: "Permission denied");

        await using var scope = ctx.CreateScope();
        var ops = scope.ServiceProvider.GetRequiredService<IWorkspaceFeatureOperations>();
        var pause = scope.ServiceProvider.GetRequiredService<IWorkspaceGitChangesMonitoringPause>();

        var result = await ops.RemoveFeatureAsync(
            featureContextId,
            new RemoveFeatureOptions { AllowDiscardUncommitted = true, AllowForceDeleteLocalBranches = true });

        Assert.False(result.Success);
        // Even though Remove failed, the pause (held in a `using`) is released so the sweep resumes.
        Assert.False(pause.IsPaused(featureContextId.Value));
    }

    private static string GetWorktreePath(object args)
    {
        var prop = args.GetType().GetProperty("worktreePath");
        return (prop?.GetValue(args) as string) ?? "";
    }

    private static async Task<WorkspaceFeatureContextId> SeedRemovableFeatureAsync(SyncStateTestContext ctx)
    {
        await using var scope = ctx.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var feature = new WorkspaceFeature
        {
            WorkspaceId = ctx.WorkspaceId,
            Name = "feat-refresh",
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
            WorktreePath = @"C:\gm-test-root\.graymoon\test-ws\features\feat-refresh\graymoon-api",
            State = WorkspaceFeatureRepositoryState.Ready,
            BaseCommitSha = "abc123",
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

    private static async Task<WorkspaceFeatureContextId> SeedRemovableFeatureWithThreeReposAsync(SyncStateTestContext ctx)
    {
        var featureContextId = await SeedRemovableFeatureAsync(ctx);
        await AddExtraRepositoryAsync(ctx, featureContextId, "graymoon-web");
        await AddExtraRepositoryAsync(ctx, featureContextId, "graymoon-cli");
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
            WorktreePath = $@"C:\gm-test-root\.graymoon\test-ws\features\feat-refresh\{repositoryName}",
            State = WorkspaceFeatureRepositoryState.Ready,
            BaseCommitSha = "def456",
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }
}
