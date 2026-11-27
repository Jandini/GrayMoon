using GrayMoon.Abstractions.Worker;
using GrayMoon.App.Data;
using GrayMoon.App.Services.GitChanges;
using GrayMoon.App.Services.Worker;
using GrayMoon.Application.Features;
using Microsoft.Extensions.DependencyInjection;

namespace GrayMoon.App.Tests;

/// <summary>
/// Create Feature keeps Git Changes monitoring of the new context off while its worktrees are checked out, and asks
/// for one scan when they exist. Watchers that started mid-checkout overflowed and git status read half-written
/// trees (logs of 2026-11-25).
/// </summary>
public sealed class CreateFeatureMonitoringPauseTests
{
    [Fact]
    public async Task Monitoring_of_the_new_context_is_paused_while_worktrees_are_created_and_one_scan_is_requested_after()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        var pause = ctx.Resolve<IWorkspaceGitChangesMonitoringPause>();
        var audits = ctx.Resolve<RecordingAuditScheduler>();
        var pausedDuringCheckout = new List<bool>();
        var auditsDuringCheckout = new List<int>();

        await RespondToHeadCommits(ctx);
        ctx.WorkerBridge.Respond(WorkerHubMethods.CreateGitWorktree, _ =>
        {
            using var scope = ctx.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var featureContextId = db.WorkspaceFeatureContexts
                .Where(c => c.WorkspaceFeatureId != null)
                .Select(c => c.WorkspaceFeatureContextId)
                .Single();
            pausedDuringCheckout.Add(pause.IsPaused(featureContextId));
            auditsDuringCheckout.Add(audits.Requests.Count);
            return new WorkerCommandResponse(true, new { success = true, worktreePath = @"C:\wt" }, null);
        });

        await using var opsScope = ctx.CreateScope();
        var ops = opsScope.ServiceProvider.GetRequiredService<IWorkspaceFeatureOperations>();
        var result = await ops.CreateFeatureAsync(ctx.WorkspaceId, "feature/paused", WorkspaceFeatureBaseKindApplication.CurrentWorkspace);

        Assert.True(result.Success, result.Error);
        Assert.Equal([true], pausedDuringCheckout);
        Assert.Equal([0], auditsDuringCheckout);
        var contextId = result.ContextId!.Value;
        Assert.False(pause.IsPaused(contextId.Value));
        Assert.Equal([(ctx.WorkspaceId, contextId.Value, GitChangesAuditReason.FeatureCreated)], audits.Requests);
    }

    [Fact]
    public async Task Repair_pauses_monitoring_while_it_retries_worktrees_and_requests_one_scan_after()
    {
        // Repair checked worktrees out into a context whose watchers and audit were running: the same overflow and
        // half-written status reads Create had before its pause.
        await using var ctx = await SyncStateTestContext.CreateAsync();
        var pause = ctx.Resolve<IWorkspaceGitChangesMonitoringPause>();
        var audits = ctx.Resolve<RecordingAuditScheduler>();
        await RespondToHeadCommits(ctx);
        ctx.WorkerBridge.Respond(WorkerHubMethods.CreateGitWorktree, new { success = false, errorMessage = "disk full" });
        WorkspaceFeatureContextId contextId;
        await using (var createScope = ctx.CreateScope())
        {
            var created = await createScope.ServiceProvider.GetRequiredService<IWorkspaceFeatureOperations>()
                .CreateFeatureAsync(ctx.WorkspaceId, "feature/repaired", WorkspaceFeatureBaseKindApplication.CurrentWorkspace);
            Assert.False(created.Success);
            contextId = created.ContextId!.Value;
        }

        var auditsBefore = audits.Requests.Count;
        var pausedDuringCheckout = new List<bool>();
        var auditsDuringCheckout = new List<int>();
        ctx.WorkerBridge.Respond(WorkerHubMethods.CreateGitWorktree, _ =>
        {
            pausedDuringCheckout.Add(pause.IsPaused(contextId.Value));
            auditsDuringCheckout.Add(audits.Requests.Count - auditsBefore);
            return new WorkerCommandResponse(true, new { success = true, worktreePath = @"C:\wt" }, null);
        });

        await using var repairScope = ctx.CreateScope();
        var repaired = await repairScope.ServiceProvider.GetRequiredService<IWorkspaceFeatureOperations>().RepairFeatureAsync(contextId);

        Assert.True(repaired.Success, repaired.Error);
        Assert.Equal([true], pausedDuringCheckout);
        Assert.Equal([0], auditsDuringCheckout);
        Assert.False(pause.IsPaused(contextId.Value));
        Assert.Equal(
            [(ctx.WorkspaceId, contextId.Value, GitChangesAuditReason.FeatureCreated)],
            audits.Requests.Skip(auditsBefore));
    }

    [Fact]
    public async Task Repair_with_nothing_to_retry_pauses_nothing_and_requests_no_scan()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        var audits = ctx.Resolve<RecordingAuditScheduler>();
        await RespondToHeadCommits(ctx);
        ctx.WorkerBridge.Respond(WorkerHubMethods.CreateGitWorktree, new { success = true, worktreePath = @"C:\wt" });
        WorkspaceFeatureContextId contextId;
        await using (var createScope = ctx.CreateScope())
        {
            var created = await createScope.ServiceProvider.GetRequiredService<IWorkspaceFeatureOperations>()
                .CreateFeatureAsync(ctx.WorkspaceId, "feature/whole", WorkspaceFeatureBaseKindApplication.CurrentWorkspace);
            Assert.True(created.Success, created.Error);
            contextId = created.ContextId!.Value;
        }

        var auditsBefore = audits.Requests.Count;
        var worktreeCalls = WorkerCommandCounts.Of(ctx.WorkerBridge).Count(WorkerHubMethods.CreateGitWorktree);

        await using var repairScope = ctx.CreateScope();
        await repairScope.ServiceProvider.GetRequiredService<IWorkspaceFeatureOperations>().RepairFeatureAsync(contextId);

        Assert.Equal(auditsBefore, audits.Requests.Count);
        Assert.Equal(worktreeCalls, WorkerCommandCounts.Of(ctx.WorkerBridge).Count(WorkerHubMethods.CreateGitWorktree));
    }

    [Fact]
    public async Task Monitoring_resumes_when_worktree_creation_throws()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        var pause = ctx.Resolve<IWorkspaceGitChangesMonitoringPause>();

        await RespondToHeadCommits(ctx);
        ctx.WorkerBridge.Respond(WorkerHubMethods.CreateGitWorktree, _ => throw new InvalidOperationException("worker gone"));

        await using var opsScope = ctx.CreateScope();
        var ops = opsScope.ServiceProvider.GetRequiredService<IWorkspaceFeatureOperations>();
        var result = await ops.CreateFeatureAsync(ctx.WorkspaceId, "feature/failed", WorkspaceFeatureBaseKindApplication.CurrentWorkspace);

        Assert.False(result.Success);
        Assert.False(pause.IsPaused(result.ContextId!.Value.Value));
    }

    private static Task RespondToHeadCommits(SyncStateTestContext ctx)
    {
        ctx.WorkerBridge.Respond(WorkerHubMethods.GetHeadCommits, new
        {
            commits = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["graymoon-api"] = "abc123def456abc123def456abc123def456abc1",
            },
            branches = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["graymoon-api"] = "develop",
            },
        });
        return ctx.StoreHeadSnapshotAsCurrentStateAsync();
    }
}
