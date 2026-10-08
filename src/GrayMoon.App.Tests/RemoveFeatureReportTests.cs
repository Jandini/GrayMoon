using GrayMoon.Abstractions.Worker;
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
        branch = "feat-refresh",
        featureBranchExists = true,
        featureBranchAheadOfDefault = 0,
        featureBranchHasUpstream = true,
        featureBranchAheadOfUpstream = 0,
    };

    [Fact]
    public async Task Branch_delete_failure_without_force_is_reported_as_kept_not_only_logged()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        var featureContextId = await SeedRemovableFeatureAsync(ctx);

        ctx.WorkerBridge.Respond(WorkerHubMethods.InspectWorktree, CleanInspectWorktree());
        ctx.WorkerBridge.Respond(WorkerHubMethods.RemoveGitWorktree, new { success = true });
        ctx.WorkerBridge.Respond(
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
    public async Task Residue_reported_by_Worker_appears_in_the_report()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        var featureContextId = await SeedRemovableFeatureAsync(ctx);

        ctx.WorkerBridge.Respond(WorkerHubMethods.InspectWorktree, CleanInspectWorktree());
        ctx.WorkerBridge.Respond(WorkerHubMethods.RemoveGitWorktree, new
        {
            success = true,
            residueRemaining = true,
            residueFileCount = 2,
            residueSampleFiles = new[] { "build.log" },
            residueMessage = "Some files could not be deleted. They may still be open in another program.",
        });
        ctx.WorkerBridge.Respond("DeleteBranch", new { success = true });

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
    public async Task Drifted_repository_reports_the_checked_out_branch_as_kept_and_still_deletes_the_Feature_branch()
    {
        // 09 SB-2: the worktree was switched to "side" instead of its Feature branch. Remove still
        // deletes the Feature branch (same as before); the report must say "side" was kept, not just
        // "removed", so the owner is not left believing the worktree's actual branch was deleted too.
        await using var ctx = await SyncStateTestContext.CreateAsync();
        var featureContextId = await SeedRemovableFeatureAsync(ctx);

        ctx.WorkerBridge.Respond(WorkerHubMethods.InspectWorktree, new
        {
            exists = true,
            isDirty = false,
            branch = "side",
            hasUpstream = true,
            aheadOfUpstream = 0,
            aheadOfDefault = 0,
            featureBranchExists = true,
            featureBranchAheadOfDefault = 1,
            featureBranchHasUpstream = false,
            featureBranchAheadOfUpstream = (int?)null,
        });
        ctx.WorkerBridge.Respond(WorkerHubMethods.RemoveGitWorktree, new { success = true });
        ctx.WorkerBridge.Respond("DeleteBranch", new { success = true });

        await using var scope = ctx.CreateScope();
        var ops = scope.ServiceProvider.GetRequiredService<IWorkspaceFeatureOperations>();
        var result = await ops.RemoveFeatureAsync(
            featureContextId,
            new RemoveFeatureOptions { AllowDiscardUncommitted = true, AllowForceDeleteLocalBranches = true });

        Assert.True(result.Success, result.Error);
        var repo = Assert.Single(result.RemoveFeatureReport!);
        Assert.True(repo.WorktreeRemoved);
        Assert.Equal(RemoveFeatureBranchOutcome.Deleted, repo.BranchOutcome);
        Assert.Equal("side", repo.KeptBranchName);
        Assert.Contains(
            ctx.WorkerBridge.Calls,
            c => c.Command == "DeleteBranch" && c.Args.GetType().GetProperty("branchName")!.GetValue(c.Args) as string == "feat-refresh");
    }

    [Fact]
    public async Task One_of_three_repos_failing_leaves_the_other_two_Removed_and_the_Feature_NeedsRepair()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        var featureContextId = await SeedRemovableFeatureWithThreeReposAsync(ctx);

        ctx.WorkerBridge.Respond(WorkerHubMethods.InspectWorktree, CleanInspectWorktree());
        ctx.WorkerBridge.Respond(WorkerHubMethods.RemoveGitWorktree, args =>
        {
            var path = GetWorktreePath(args);
            if (path.EndsWith("graymoon-web", StringComparison.Ordinal))
                return new WorkerCommandResponse(false, null, "Permission denied on graymoon-web");
            return new WorkerCommandResponse(true, new { success = true }, null);
        });
        ctx.WorkerBridge.Respond("DeleteBranch", new { success = true });

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
    public async Task Second_Remove_skips_an_already_Removed_row_and_issues_zero_Worker_calls_for_it()
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

        ctx.WorkerBridge.Respond(WorkerHubMethods.InspectWorktree, CleanInspectWorktree());
        ctx.WorkerBridge.Respond(WorkerHubMethods.RemoveGitWorktree, new { success = true });
        ctx.WorkerBridge.Respond("DeleteBranch", new { success = true });

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
            ctx.WorkerBridge.Calls,
            c => c.Command == WorkerHubMethods.RemoveGitWorktree && GetWorktreePath(c.Args) == removedWorktreePath);
        Assert.Equal(1, ctx.WorkerBridge.Calls.Count(c => c.Command == WorkerHubMethods.RemoveGitWorktree));
        Assert.Equal(1, ctx.WorkerBridge.Calls.Count(c => c.Command == "DeleteBranch"));

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

        ctx.WorkerBridge.Respond(WorkerHubMethods.InspectWorktree, CleanInspectWorktree());
        ctx.WorkerBridge.Respond(WorkerHubMethods.RemoveGitWorktree, new { success = true });
        ctx.WorkerBridge.Respond("DeleteBranch", new { success = true });

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

        ctx.WorkerBridge.Respond(WorkerHubMethods.InspectWorktree, new
        {
            exists = true,
            isLocked = true,
            lockReason = "testing",
            isDirty = false,
            branch = "feat-refresh",
            hasUpstream = true,
            aheadOfUpstream = 0,
            aheadOfDefault = 0,
            featureBranchExists = true,
            featureBranchAheadOfDefault = 0,
            featureBranchHasUpstream = true,
            featureBranchAheadOfUpstream = 0,
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

        ctx.WorkerBridge.Respond(WorkerHubMethods.InspectWorktree, CleanInspectWorktree());

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
        // only forwards the user's consent to the Worker, which runs git worktree unlock.
        await using var ctx = await SyncStateTestContext.CreateAsync();
        var featureContextId = await SeedRemovableFeatureAsync(ctx);

        ctx.WorkerBridge.Respond(WorkerHubMethods.InspectWorktree, new
        {
            exists = true,
            isLocked = true,
            lockReason = "testing",
            isDirty = false,
            branch = "feat-refresh",
            hasUpstream = true,
            aheadOfUpstream = 0,
            aheadOfDefault = 0,
            featureBranchExists = true,
            featureBranchAheadOfDefault = 0,
            featureBranchHasUpstream = true,
            featureBranchAheadOfUpstream = 0,
        });
        ctx.WorkerBridge.Respond(WorkerHubMethods.RemoveGitWorktree, new { success = true });
        ctx.WorkerBridge.Respond("DeleteBranch", new { success = true });

        await using var scope = ctx.CreateScope();
        var ops = scope.ServiceProvider.GetRequiredService<IWorkspaceFeatureOperations>();
        var result = await ops.RemoveFeatureAsync(
            featureContextId,
            new RemoveFeatureOptions { AllowUnlockWorktrees = true });

        Assert.True(result.Success, result.Error);
        var call = Assert.Single(ctx.WorkerBridge.Calls, c => c.Command == WorkerHubMethods.RemoveGitWorktree);
        var unlockProp = call.Args.GetType().GetProperty("unlock");
        Assert.NotNull(unlockProp);
        Assert.Equal(true, unlockProp!.GetValue(call.Args));
    }

    [Fact]
    public async Task Failed_remove_restarts_the_Feature_context_monitoring()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        var featureContextId = await SeedRemovableFeatureAsync(ctx);

        ctx.WorkerBridge.Respond(WorkerHubMethods.InspectWorktree, CleanInspectWorktree());
        ctx.WorkerBridge.Respond(
            WorkerHubMethods.RemoveGitWorktree,
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

    // ---- Blocker diagnostics (processes keeping a worktree in use) --------------------------------

    private static object InUseFailure() => new
    {
        success = false,
        errorCode = "GitFailed",
        errorMessage = "error: failed to delete 'C:/f/repo': Permission denied",
        failureKind = "PathInUse",
        blockingProcesses = new[]
        {
            new { processId = 18472, processName = "claude", executablePath = @"C:\tools\claude.exe", serviceName = (string?)null, kind = "Application", reason = "WorkingDirectory" },
            new { processId = 22140, processName = "dotnet", executablePath = @"C:\Program Files\dotnet\dotnet.exe", serviceName = (string?)null, kind = "Console", reason = "OpenFile" },
        },
        blockersMayBeIncomplete = false,
    };

    [Fact]
    public async Task In_use_failure_returns_the_blocking_processes_and_keeps_the_original_error()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        var featureContextId = await SeedRemovableFeatureAsync(ctx);

        ctx.WorkerBridge.Respond(WorkerHubMethods.InspectWorktree, CleanInspectWorktree());
        ctx.WorkerBridge.Respond(WorkerHubMethods.RemoveGitWorktree, _ =>
            new WorkerCommandResponse(false, InUseFailure(), "error: failed to delete 'C:/f/repo': Permission denied"));

        await using var scope = ctx.CreateScope();
        var ops = scope.ServiceProvider.GetRequiredService<IWorkspaceFeatureOperations>();
        var result = await ops.RemoveFeatureAsync(
            featureContextId,
            new RemoveFeatureOptions { AllowDiscardUncommitted = true, AllowForceDeleteLocalBranches = true });

        Assert.False(result.Success);
        Assert.Contains("Permission denied", result.Error);
        var repo = Assert.Single(result.RemoveFeatureBlockers!);
        Assert.Equal("graymoon-api", repo.RepositoryName);
        Assert.EndsWith("graymoon-api", repo.WorktreePath);
        Assert.Equal(2, repo.Processes.Count);
        var claude = Assert.Single(repo.Processes, p => p.ProcessId == 18472);
        Assert.True(claude.IsWorkingDirectory);
        Assert.Equal(@"C:\tools\claude.exe", claude.ExecutablePath);
        Assert.Contains(repo.Processes, p => p.ProcessId == 22140 && !p.IsWorkingDirectory);

        // The Feature is kept for Retry, exactly as before blocker diagnostics existed.
        await using var read = ctx.CreateScope();
        var db = read.ServiceProvider.GetRequiredService<AppDbContext>();
        var feature = await db.WorkspaceFeatures.SingleAsync(f => f.Name == "feat-refresh" && f.WorkspaceId == ctx.WorkspaceId);
        Assert.Equal(WorkspaceFeatureLifecycleState.NeedsRepair, feature.LifecycleState);
    }

    [Fact]
    public async Task Failure_without_blockers_from_the_Worker_has_no_blocker_list()
    {
        // An old Worker, or a failure the Worker did not classify as "in use", sends no blockingProcesses.
        await using var ctx = await SyncStateTestContext.CreateAsync();
        var featureContextId = await SeedRemovableFeatureAsync(ctx);

        ctx.WorkerBridge.Respond(WorkerHubMethods.InspectWorktree, CleanInspectWorktree());
        ctx.WorkerBridge.Respond(WorkerHubMethods.RemoveGitWorktree, _ =>
            new WorkerCommandResponse(false, new { success = false, errorMessage = "fatal: validation failed" }, "fatal: validation failed"));

        await using var scope = ctx.CreateScope();
        var ops = scope.ServiceProvider.GetRequiredService<IWorkspaceFeatureOperations>();
        var result = await ops.RemoveFeatureAsync(
            featureContextId,
            new RemoveFeatureOptions { AllowDiscardUncommitted = true, AllowForceDeleteLocalBranches = true });

        Assert.False(result.Success);
        Assert.Equal("fatal: validation failed", result.Error);
        Assert.Null(result.RemoveFeatureBlockers);
        Assert.DoesNotContain(ctx.WorkerBridge.Calls, c => c.Command == WorkerHubMethods.InspectPathLocks);
    }

    [Fact]
    public async Task Clean_remove_never_asks_the_Worker_for_blockers()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        var featureContextId = await SeedRemovableFeatureAsync(ctx);

        ctx.WorkerBridge.Respond(WorkerHubMethods.InspectWorktree, CleanInspectWorktree());
        ctx.WorkerBridge.Respond(WorkerHubMethods.RemoveGitWorktree, new { success = true });
        ctx.WorkerBridge.Respond("DeleteBranch", new { success = true });

        await using var scope = ctx.CreateScope();
        var ops = scope.ServiceProvider.GetRequiredService<IWorkspaceFeatureOperations>();
        var result = await ops.RemoveFeatureAsync(
            featureContextId,
            new RemoveFeatureOptions { AllowDiscardUncommitted = true, AllowForceDeleteLocalBranches = true });

        Assert.True(result.Success, result.Error);
        Assert.Null(result.RemoveFeatureBlockers);
        var repo = Assert.Single(result.RemoveFeatureReport!);
        Assert.Null(repo.BlockingProcesses);
        Assert.Null(repo.ResidueTarget);
        Assert.DoesNotContain(ctx.WorkerBridge.Calls, c => c.Command == WorkerHubMethods.InspectPathLocks);
    }

    [Fact]
    public async Task Leftover_files_report_carries_blockers_and_a_retry_target()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        var featureContextId = await SeedRemovableFeatureAsync(ctx);

        ctx.WorkerBridge.Respond(WorkerHubMethods.InspectWorktree, CleanInspectWorktree());
        ctx.WorkerBridge.Respond(WorkerHubMethods.RemoveGitWorktree, new
        {
            success = true,
            residueRemaining = true,
            residueFileCount = 0,
            residueMessage = "The worktree folder could not be removed.",
            blockingProcesses = new[]
            {
                new { processId = 31337, processName = "pwsh", executablePath = (string?)null, serviceName = (string?)null, kind = "Application", reason = "WorkingDirectory" },
            },
            blockersMayBeIncomplete = true,
            blockersDiagnostic = "Only the first 4000 files were checked.",
        });
        ctx.WorkerBridge.Respond("DeleteBranch", new { success = true });

        await using var scope = ctx.CreateScope();
        var ops = scope.ServiceProvider.GetRequiredService<IWorkspaceFeatureOperations>();
        var result = await ops.RemoveFeatureAsync(
            featureContextId,
            new RemoveFeatureOptions { AllowDiscardUncommitted = true, AllowForceDeleteLocalBranches = true });

        Assert.True(result.Success, result.Error);
        var repo = Assert.Single(result.RemoveFeatureReport!);
        Assert.True(repo.ResidueRemaining);
        var blocker = Assert.Single(repo.BlockingProcesses!);
        Assert.Equal(31337, blocker.ProcessId);
        Assert.True(blocker.IsWorkingDirectory);
        Assert.True(repo.BlockersMayBeIncomplete);
        Assert.Equal("Only the first 4000 files were checked.", repo.BlockersDiagnostic);

        var call = Assert.Single(ctx.WorkerBridge.Calls, c => c.Command == WorkerHubMethods.RemoveGitWorktree);
        var sentFeatureRoot = call.Args.GetType().GetProperty("featureRootPath")!.GetValue(call.Args) as string;
        if (sentFeatureRoot is null)
        {
            // Without the Feature storage paths the Worker only reports leftovers, so Retry could not help.
            Assert.Null(repo.ResidueTarget);
        }
        else
        {
            Assert.NotNull(repo.ResidueTarget);
            Assert.Equal(GetWorktreePath(call.Args), repo.ResidueTarget!.WorktreePath);
            Assert.Equal(sentFeatureRoot, repo.ResidueTarget.FeatureRootPath);
        }
    }

    [Fact]
    public async Task Retry_leftovers_resends_the_guarded_cleanup_and_clears_residue_when_it_succeeds()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        ctx.WorkerBridge.Respond(WorkerHubMethods.RemoveGitWorktree, new { success = true, alreadyRemoved = true, residueRemaining = false });

        var target = new RemoveFeatureResidueTarget(@"C:\ws\api", @"C:\f\feat\api", @"C:\f\feat", @"C:\f");
        IReadOnlyList<RemoveFeatureRepositoryReport> report =
        [
            new RemoveFeatureRepositoryReport(1, "api", true, RemoveFeatureBranchOutcome.Deleted, null, true, 1, ["a.dll"], "in use")
            {
                BlockingProcesses = [new RemoveFeatureBlockingProcess(9, "dotnet", null, null, "Console", "OpenFile")],
                ResidueTarget = target,
            },
            new RemoveFeatureRepositoryReport(2, "web", true, RemoveFeatureBranchOutcome.Kept, null, false, 0, null, null),
        ];

        await using var scope = ctx.CreateScope();
        var ops = scope.ServiceProvider.GetRequiredService<IWorkspaceFeatureOperations>();
        var updated = await ops.RetryRemoveFeatureResidueAsync(report);

        var call = Assert.Single(ctx.WorkerBridge.Calls, c => c.Command == WorkerHubMethods.RemoveGitWorktree);
        Assert.Equal(target.WorktreePath, GetWorktreePath(call.Args));
        Assert.Equal(target.FeatureStorageRoot, call.Args.GetType().GetProperty("featureStorageRoot")!.GetValue(call.Args));
        Assert.Equal(false, call.Args.GetType().GetProperty("force")!.GetValue(call.Args));

        Assert.False(updated[0].ResidueRemaining);
        Assert.Null(updated[0].ResidueTarget);
        Assert.Null(updated[0].BlockingProcesses);
        Assert.Same(report[1], updated[1]);
    }

    [Fact]
    public async Task Retry_leftovers_keeps_the_entry_and_its_target_when_files_are_still_in_use()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        ctx.WorkerBridge.Respond(WorkerHubMethods.RemoveGitWorktree, new
        {
            success = true,
            alreadyRemoved = true,
            residueRemaining = true,
            residueFileCount = 1,
            residueMessage = "Some files could not be deleted. They may still be open in another program.",
            blockingProcesses = new[] { new { processId = 9, processName = "dotnet", kind = "Console", reason = "OpenFile" } },
        });

        var target = new RemoveFeatureResidueTarget(@"C:\ws\api", @"C:\f\feat\api", @"C:\f\feat", @"C:\f");
        IReadOnlyList<RemoveFeatureRepositoryReport> report =
        [
            new RemoveFeatureRepositoryReport(1, "api", true, RemoveFeatureBranchOutcome.Deleted, null, true, 3, null, "in use")
            {
                ResidueTarget = target,
            },
        ];

        await using var scope = ctx.CreateScope();
        var ops = scope.ServiceProvider.GetRequiredService<IWorkspaceFeatureOperations>();
        var updated = await ops.RetryRemoveFeatureResidueAsync(report);

        var entry = Assert.Single(updated);
        Assert.True(entry.ResidueRemaining);
        Assert.Equal(1, entry.ResidueFileCount);
        Assert.Equal(target, entry.ResidueTarget);
        Assert.Equal(9, Assert.Single(entry.BlockingProcesses!).ProcessId);
    }

    [Fact]
    public async Task Inspect_blockers_maps_Worker_results_and_marks_gone_folders()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        ctx.WorkerBridge.Respond(WorkerHubMethods.InspectPathLocks, new
        {
            success = true,
            results = new object[]
            {
                new { path = @"C:\f\a", exists = true, blockingProcesses = new[] { new { processId = 5, processName = "Code", kind = "Application", reason = "OpenFile" } }, mayBeIncomplete = false },
                new { path = @"C:\f\b", exists = false, blockingProcesses = Array.Empty<object>(), mayBeIncomplete = false },
            },
        });

        IReadOnlyList<RemoveFeatureRepositoryBlockers> targets =
        [
            new(1, "a", @"C:\f\a", [], false, null),
            new(2, "b", @"C:\f\b", [new RemoveFeatureBlockingProcess(7, "old", null, null, "Console", "OpenFile")], false, null),
        ];

        await using var scope = ctx.CreateScope();
        var ops = scope.ServiceProvider.GetRequiredService<IWorkspaceFeatureOperations>();
        var refreshed = await ops.InspectRemoveFeatureBlockersAsync(targets);

        Assert.Equal(5, Assert.Single(refreshed[0].Processes).ProcessId);
        Assert.True(refreshed[0].PathExists);
        Assert.False(refreshed[1].PathExists);
        Assert.Empty(refreshed[1].Processes);
    }

    [Fact]
    public async Task Inspect_blockers_on_an_old_Worker_keeps_the_last_list_and_says_it_may_be_incomplete()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        ctx.WorkerBridge.Respond(WorkerHubMethods.InspectPathLocks, data: null, success: false, error: "Unknown command: InspectPathLocks");

        var previous = new RemoveFeatureBlockingProcess(7, "dotnet", null, null, "Console", "OpenFile");
        IReadOnlyList<RemoveFeatureRepositoryBlockers> targets = [new(1, "a", @"C:\f\a", [previous], false, null)];

        await using var scope = ctx.CreateScope();
        var ops = scope.ServiceProvider.GetRequiredService<IWorkspaceFeatureOperations>();
        var refreshed = await ops.InspectRemoveFeatureBlockersAsync(targets);

        var entry = Assert.Single(refreshed);
        Assert.Same(previous, Assert.Single(entry.Processes));
        Assert.True(entry.MayBeIncomplete);
        Assert.False(string.IsNullOrWhiteSpace(entry.Diagnostic));
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
