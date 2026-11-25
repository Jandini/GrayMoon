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

        RespondToHeadCommits(ctx);
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
    public async Task Monitoring_resumes_when_worktree_creation_throws()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        var pause = ctx.Resolve<IWorkspaceGitChangesMonitoringPause>();

        RespondToHeadCommits(ctx);
        ctx.WorkerBridge.Respond(WorkerHubMethods.CreateGitWorktree, _ => throw new InvalidOperationException("worker gone"));

        await using var opsScope = ctx.CreateScope();
        var ops = opsScope.ServiceProvider.GetRequiredService<IWorkspaceFeatureOperations>();
        var result = await ops.CreateFeatureAsync(ctx.WorkspaceId, "feature/failed", WorkspaceFeatureBaseKindApplication.CurrentWorkspace);

        Assert.False(result.Success);
        Assert.False(pause.IsPaused(result.ContextId!.Value.Value));
    }

    private static void RespondToHeadCommits(SyncStateTestContext ctx) =>
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
}
