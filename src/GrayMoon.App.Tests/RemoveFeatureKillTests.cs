using System.Text.Json;
using GrayMoon.Abstractions.Worker;
using GrayMoon.App.Components.Features;
using GrayMoon.App.Data;
using GrayMoon.Application;
using GrayMoon.Application.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GrayMoon.App.Tests;

/// <summary>
/// "Kill or Continue" in Remove Feature: the dialog helpers that decide when to stop at the blocker step and what Kill sends,
/// and the App operations that resolve the folders from the database (or the removal report), never from the caller.
/// </summary>
public sealed class RemoveFeatureKillTests
{
    private static readonly DateTime Started = new(2026, 10, 8, 12, 0, 0, DateTimeKind.Utc);

    private static RemoveFeatureBlockingProcess Process(int id, bool canTerminate = true, string? protectedReason = null) =>
        new(id, $"p{id}", null, null, "Application", "OpenFile")
        {
            StartTimeUtc = Started.AddSeconds(id),
            CanTerminate = canTerminate,
            ProtectedReason = protectedReason,
        };

    private static RemoveFeatureRepositoryBlockers Repo(int id, params RemoveFeatureBlockingProcess[] processes) =>
        new(id, $"repo{id}", $@"C:\f\repo{id}", processes, false, null);

    [Fact]
    public void Blocker_step_is_shown_for_programs_or_a_failed_check_but_not_for_a_clean_result()
    {
        Assert.False(RemoveFeatureBlockerText.NeedsBlockerStep([Repo(1)]));
        Assert.False(RemoveFeatureBlockerText.NeedsBlockerStep([]));
        Assert.True(RemoveFeatureBlockerText.NeedsBlockerStep([Repo(1, Process(5))]));
        Assert.True(RemoveFeatureBlockerText.NeedsBlockerStep(null));
        Assert.True(RemoveFeatureBlockerText.NeedsBlockerStep([Repo(1) with { LookupFailed = true }]));
        Assert.False(RemoveFeatureBlockerText.NeedsBlockerStep([Repo(1, Process(5)) with { PathExists = false }]));
    }

    [Fact]
    public void Default_selection_is_every_endable_program_and_kill_sends_their_start_times()
    {
        var shown = RemoveFeatureBlockerText.ShownProcesses(
        [
            Repo(1, Process(5), Process(6, canTerminate: false, protectedReason: "Explorer")),
            Repo(2, Process(5), Process(7)),
        ]);

        var selected = new HashSet<int>(RemoveFeatureBlockerText.DefaultSelection(shown));
        Assert.Equal([5, 7], selected.Order());

        selected.Remove(7);
        selected.Add(6); // a protected process is never sent even if somehow ticked
        var selections = RemoveFeatureBlockerText.Selections(shown, selected);

        Assert.Equal([new RemoveFeatureProcessSelection(5, Started.AddSeconds(5))], selections);
    }

    [Fact]
    public void Protected_and_outcome_text_is_plain_language()
    {
        Assert.Contains("Close the window", RemoveFeatureBlockerText.ProtectedText("Explorer"));
        Assert.Contains("administrator", RemoveFeatureBlockerText.ProtectedText("AccessDenied"));
        Assert.Contains("Close it yourself", RemoveFeatureBlockerText.ProtectedText(null));
        Assert.Null(RemoveFeatureBlockerText.OutcomeText(RemoveFeatureProcessOutcome.Killed));
        Assert.Contains("restarted", RemoveFeatureBlockerText.OutcomeText(RemoveFeatureProcessOutcome.StartTimeChanged));
        Assert.Equal("Ended 2 programs.", RemoveFeatureBlockerText.KillSummary(
        [
            new(1, "a", RemoveFeatureProcessOutcome.Killed),
            new(2, "b", RemoveFeatureProcessOutcome.Killed),
            new(3, "c", RemoveFeatureProcessOutcome.AccessDenied),
        ]));
        Assert.Null(RemoveFeatureBlockerText.KillSummary([new(3, "c", RemoveFeatureProcessOutcome.AccessDenied)]));
        Assert.Equal(
            "Runs a program or library from this Feature",
            RemoveFeatureBlockerText.Reason(new RemoveFeatureBlockingProcess(1, "x", null, null, "Application", "LoadedModule")));
    }

    [Fact]
    public void Kill_and_continue_text_explains_the_consequences_in_ascii()
    {
        Assert.Contains("Unsaved work", RemoveFeatureBlockerText.KillExplanation);
        Assert.Contains("left on disk", RemoveFeatureBlockerText.ContinueExplanation);
        Assert.Contains("leave files behind", RemoveFeatureBlockerText.CheckFailedBody);

        var texts = new[]
        {
            RemoveFeatureBlockerText.PreflightTitle, RemoveFeatureBlockerText.PreflightBody, RemoveFeatureBlockerText.Checking,
            RemoveFeatureBlockerText.CheckFailedTitle, RemoveFeatureBlockerText.CheckFailedBody, RemoveFeatureBlockerText.KillExplanation,
            RemoveFeatureBlockerText.ContinueExplanation, RemoveFeatureBlockerText.LeftoverKillExplanation,
        };
        Assert.All(texts, t => Assert.DoesNotContain(t, c => c is '\u2013' or '\u2014'));
    }

    [Fact]
    public async Task Feature_check_and_kill_use_the_feature_folders_from_the_database()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        var contextId = await CreateFeatureAsync(ctx);
        string worktreePath;
        await using (var read = ctx.CreateScope())
        {
            var db = read.ServiceProvider.GetRequiredService<AppDbContext>();
            worktreePath = (await db.WorkspaceFeatureRepositories.SingleAsync(r => r.WorkspaceFeatureContextId == contextId.Value)).WorktreePath;
        }

        ctx.WorkerBridge.Respond(WorkerHubMethods.InspectPathLocks, new
        {
            success = true,
            results = new[] { new { path = worktreePath, exists = true, blockingProcesses = new[] { Wire(42) }, mayBeIncomplete = false } },
        });
        ctx.WorkerBridge.Respond(WorkerHubMethods.TerminateBlockingProcesses, new
        {
            success = true,
            outcomes = new[] { new { processId = 42, processName = "claude", outcome = "Killed" } },
            results = new[] { new { path = worktreePath, exists = true, blockingProcesses = Array.Empty<object>(), mayBeIncomplete = false } },
        });

        await using var scope = ctx.CreateScope();
        var ops = scope.ServiceProvider.GetRequiredService<IWorkspaceFeatureOperations>();

        var blockers = await ops.InspectFeatureBlockersAsync(contextId);
        var process = Assert.Single(Assert.Single(blockers).Processes);
        Assert.Equal(42, process.ProcessId);
        Assert.True(process.CanTerminate);
        Assert.Equal(Started, process.StartTimeUtc);

        var killed = await ops.TerminateFeatureBlockersAsync(contextId, [new RemoveFeatureProcessSelection(42, Started)]);

        Assert.True(killed.Success, killed.Error);
        Assert.True(Assert.Single(killed.Outcomes).IsResolved);
        Assert.False(RemoveFeatureBlockerText.NeedsBlockerStep(killed.Remaining));
        var sent = Args(ctx.WorkerBridge.Calls.Last(c => c.Command == WorkerHubMethods.TerminateBlockingProcesses).Args);
        Assert.Equal([worktreePath], sent.GetProperty("paths").EnumerateArray().Select(p => p.GetString()));
        var selection = Assert.Single(sent.GetProperty("processes").EnumerateArray());
        Assert.Equal(42, selection.GetProperty("processId").GetInt32());
        Assert.Equal(Started, selection.GetProperty("startTimeUtc").GetDateTime().ToUniversalTime());
    }

    [Fact]
    public async Task Leftover_kill_uses_only_the_report_residue_targets()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        ctx.WorkerBridge.Respond(WorkerHubMethods.TerminateBlockingProcesses, new
        {
            success = true,
            outcomes = new[] { new { processId = 42, processName = "claude", outcome = "AccessDenied" } },
            results = new[] { new { path = @"C:\f\api", exists = true, blockingProcesses = new[] { Wire(42) }, mayBeIncomplete = false } },
        });
        IReadOnlyList<RemoveFeatureRepositoryReport> report =
        [
            Leftover(1, "api", new RemoveFeatureResidueTarget(@"C:\main\api", @"C:\f\api", @"C:\f", @"C:\")),
            Leftover(2, "root", target: null),
        ];

        await using var scope = ctx.CreateScope();
        var ops = scope.ServiceProvider.GetRequiredService<IWorkspaceFeatureOperations>();
        var result = await ops.TerminateLeftoverBlockersAsync(report, [new RemoveFeatureProcessSelection(42, Started)]);

        Assert.True(result.Success, result.Error);
        Assert.False(Assert.Single(result.Outcomes).IsResolved);
        Assert.True(RemoveFeatureBlockerText.AnyProcesses(result.Remaining));
        var sent = Args(Assert.Single(ctx.WorkerBridge.Calls, c => c.Command == WorkerHubMethods.TerminateBlockingProcesses).Args);
        Assert.Equal([@"C:\f\api"], sent.GetProperty("paths").EnumerateArray().Select(p => p.GetString()));
    }

    [Fact]
    public async Task A_failed_lookup_or_kill_is_reported_as_could_not_check_never_thrown()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        ctx.WorkerBridge.Respond(WorkerHubMethods.InspectPathLocks, data: null, success: false, error: "Worker not connected.");
        ctx.WorkerBridge.Respond(WorkerHubMethods.TerminateBlockingProcesses, data: null, success: false, error: "Worker not connected.");
        IReadOnlyList<RemoveFeatureRepositoryBlockers> targets = [Repo(1)];

        await using var scope = ctx.CreateScope();
        var ops = scope.ServiceProvider.GetRequiredService<IWorkspaceFeatureOperations>();

        var looked = await ops.InspectRemoveFeatureBlockersAsync(targets);
        Assert.True(Assert.Single(looked).LookupFailed);
        Assert.True(RemoveFeatureBlockerText.NeedsBlockerStep(looked));

        var killed = await ops.TerminateLeftoverBlockersAsync(
            [Leftover(1, "api", new RemoveFeatureResidueTarget(@"C:\main\api", @"C:\f\api", @"C:\f", @"C:\"))],
            [new RemoveFeatureProcessSelection(42, Started)]);
        Assert.False(killed.Success);
        Assert.Contains("Worker not connected", killed.Error);
        Assert.Empty(killed.Outcomes);
    }

    // ---- helpers --------------------------------------------------------------------------------

    private static object Wire(int processId) => new
    {
        processId,
        processName = "claude",
        kind = "Application",
        reason = "WorkingDirectory",
        startTimeUtc = Started,
        canTerminate = true,
    };

    private static RemoveFeatureRepositoryReport Leftover(int id, string name, RemoveFeatureResidueTarget? target) =>
        new(id, name, WorktreeRemoved: true, RemoveFeatureBranchOutcome.Deleted, null, ResidueRemaining: true, 1, ["a.txt"], "In use.")
        {
            ResidueTarget = target,
        };

    private static JsonElement Args(object args) => JsonSerializer.SerializeToElement(args);

    private static async Task<WorkspaceFeatureContextId> CreateFeatureAsync(SyncStateTestContext ctx)
    {
        ctx.WorkerBridge.Respond(WorkerHubMethods.GetHeadCommits, new
        {
            commits = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["graymoon-api"] = "abc123def456abc123def456abc123def456abc1",
            },
            branches = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["graymoon-api"] = "main" },
        });
        ctx.WorkerBridge.Respond(WorkerHubMethods.CreateGitWorktree, new { success = true, worktreePath = @"C:\wt" });

        await using var scope = ctx.CreateScope();
        var ops = scope.ServiceProvider.GetRequiredService<IWorkspaceFeatureOperations>();
        var result = await ops.CreateFeatureAsync(ctx.WorkspaceId, "feature/kill", WorkspaceFeatureBaseKindApplication.CurrentWorkspace);
        Assert.True(result.Success, result.Error);
        return result.ContextId!.Value;
    }
}
