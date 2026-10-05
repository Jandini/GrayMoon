using GrayMoon.Abstractions.Worker;
using GrayMoon.Application.Features;
using Microsoft.Extensions.DependencyInjection;

namespace GrayMoon.App.Tests;

/// <summary>
/// External-worktree cleanup disk facts (exists / dirty) come from the Worker's InspectWorktree
/// command, never from App-side disk access, so behaviour is identical whether the App runs next
/// to the developer's disk or in a Docker container (A2).
/// </summary>
public sealed class ExternalWorktreeCleanupTests
{
    private static object CleanInspectWorktree(bool exists = true, bool? isDirty = false) => new
    {
        exists,
        isDirty,
    };

    [Fact]
    public async Task Analyze_reports_dirty_from_Worker_InspectWorktree_response()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        const string worktreePath = @"C:\gm-test-root\external\stray-worktree";
        ctx.WorkerBridge.Respond(WorkerHubMethods.InspectWorktree, CleanInspectWorktree(exists: true, isDirty: true));

        await using var scope = ctx.CreateScope();
        var ops = scope.ServiceProvider.GetRequiredService<IWorkspaceExternalWorktreeOperations>();
        var plan = await ops.AnalyzeExternalWorktreeCleanupAsync(ctx.WorkspaceId, ctx.WorkspaceRepositoryId, worktreePath);

        Assert.True(plan.Success, plan.Error);
        Assert.True(plan.WorktreeExists);
        Assert.True(plan.IsDirty);
        Assert.False(plan.WorktreeStatusUnknown);
        Assert.True(plan.RequiresForce);
        Assert.False(plan.CanRemoveNormally);
    }

    [Fact]
    public async Task Analyze_marks_disk_state_Unknown_when_Worker_is_disconnected()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        const string worktreePath = @"C:\gm-test-root\external\stray-worktree";
        ctx.WorkerBridge.IsWorkerConnected = false;

        await using var scope = ctx.CreateScope();
        var ops = scope.ServiceProvider.GetRequiredService<IWorkspaceExternalWorktreeOperations>();
        var plan = await ops.AnalyzeExternalWorktreeCleanupAsync(ctx.WorkspaceId, ctx.WorkspaceRepositoryId, worktreePath);

        Assert.True(plan.Success, plan.Error);
        Assert.True(plan.WorktreeStatusUnknown);
    }

    [Fact]
    public async Task Remove_non_forced_still_works_when_old_Worker_does_not_support_InspectWorktree()
    {
        // Regression guard R-A2a/R-A2b: an old Worker answers "Unknown command" for InspectWorktree.
        // Non-forced external-worktree cleanup must keep working exactly as before (Git itself
        // refuses to remove a dirty worktree without --force).
        await using var ctx = await SyncStateTestContext.CreateAsync();
        const string worktreePath = @"C:\gm-test-root\external\stray-worktree";
        ctx.WorkerBridge.Respond(
            WorkerHubMethods.InspectWorktree,
            data: null,
            success: false,
            error: "Unknown command: InspectWorktree");
        ctx.WorkerBridge.Respond(WorkerHubMethods.RemoveGitWorktree, new { success = true });

        await using var scope = ctx.CreateScope();
        var ops = scope.ServiceProvider.GetRequiredService<IWorkspaceExternalWorktreeOperations>();
        var result = await ops.RemoveExternalWorktreeAsync(
            ctx.WorkspaceId,
            ctx.WorkspaceRepositoryId,
            worktreePath,
            new ExternalWorktreeCleanupOptions { AllowForceRemoveDirty = false });

        Assert.True(result.Success, result.Error);
        Assert.Contains(ctx.WorkerBridge.Calls, c => c.Command == WorkerHubMethods.RemoveGitWorktree);
    }

    [Fact]
    public async Task Remove_forced_is_blocked_when_old_Worker_does_not_support_InspectWorktree()
    {
        // Regression guard R-A2a/R-A2b: an explicit force/discard request cannot be honored when the
        // Worker cannot confirm the worktree is actually dirty, so it is refused with an "update the
        // Worker" message instead of blindly force-removing.
        await using var ctx = await SyncStateTestContext.CreateAsync();
        const string worktreePath = @"C:\gm-test-root\external\stray-worktree";
        ctx.WorkerBridge.Respond(
            WorkerHubMethods.InspectWorktree,
            data: null,
            success: false,
            error: "Unknown command: InspectWorktree");

        await using var scope = ctx.CreateScope();
        var ops = scope.ServiceProvider.GetRequiredService<IWorkspaceExternalWorktreeOperations>();
        var result = await ops.RemoveExternalWorktreeAsync(
            ctx.WorkspaceId,
            ctx.WorkspaceRepositoryId,
            worktreePath,
            new ExternalWorktreeCleanupOptions { AllowForceRemoveDirty = true });

        Assert.False(result.Success);
        Assert.Contains("Update the Worker", result.Error);
        Assert.DoesNotContain(ctx.WorkerBridge.Calls, c => c.Command == WorkerHubMethods.RemoveGitWorktree);
    }
}
