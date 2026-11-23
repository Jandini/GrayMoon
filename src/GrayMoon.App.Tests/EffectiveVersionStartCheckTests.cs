using System.Net;
using GrayMoon.Abstractions.Worker;
using GrayMoon.App.Models;
using GrayMoon.App.Services.Jobs;
using GrayMoon.App.Services.Orchestration;
using GrayMoon.App.Services.Versions;
using GrayMoon.App.Tests.Policy.Characterization;
using Microsoft.Extensions.DependencyInjection;

namespace GrayMoon.App.Tests;

/// <summary>
/// The start check (D3, I8, plan F1): a registry answer that is neither "available" nor "not found" stops Update (and
/// with it Update and Push and Prepare Workspace, which run the same update) and bulk Push before anything changes.
/// </summary>
public sealed class EffectiveVersionStartCheckTests
{
    [Fact]
    public async Task Bulk_push_stops_before_any_push_when_the_registry_refuses_the_credentials()
    {
        await using var s = await EffectiveVersionScenario.CreateAsync(configureServices: PolicyHarness.AddFacades);
        s.Registry.Answers(EffectiveVersionScenario.PackageId, HttpStatusCode.Forbidden);

        await using var scope = s.Ctx.CreateScope();
        var plan = await scope.ServiceProvider.GetRequiredService<IWorkspacePushOperations>().GetPlanAsync(s.Ctx.WorkspaceId, s.Special);

        Assert.False(plan.HasUnpushed);
        Assert.Empty(plan.RepositoryIds);
        Assert.Contains("connector nuget-test returned HTTP 403", plan.StopMessage);
        Assert.DoesNotContain(s.Ctx.WorkerBridge.Calls, c => c.Command == "PushRepository");
    }

    [Fact]
    public async Task Bulk_push_plan_of_a_fresh_resolvable_context_is_empty_and_says_why()
    {
        await using var s = await EffectiveVersionScenario.CreateAsync(configureServices: PolicyHarness.AddFacades);

        await using var scope = s.Ctx.CreateScope();
        var plan = await scope.ServiceProvider.GetRequiredService<IWorkspacePushOperations>().GetPlanAsync(s.Ctx.WorkspaceId, s.Special);

        Assert.Null(plan.StopMessage);
        Assert.False(plan.HasUnpushed);
        Assert.Equal(2, plan.NoWork.Count);
    }

    [Fact]
    public async Task Bulk_push_plan_counts_the_GitVersion_runs_and_writes_every_decision_to_the_job_terminal()
    {
        await using var s = await EffectiveVersionScenario.CreateAsync(configureServices: PolicyHarness.AddFacades);
        var steps = new RecordingProgress();
        var terminal = new JobTerminalBuffer();

        await using var scope = s.Ctx.CreateScope();
        using (TerminalSinkContext.Use(terminal))
            await scope.ServiceProvider.GetRequiredService<IWorkspacePushOperations>().GetPlanAsync(s.Ctx.WorkspaceId, s.Special, progress: steps);

        Assert.Equal(["Checking versions...", "Checked 1 of 1 repositories..."], steps.Messages);
        var lines = terminal.GetSnapshot();
        Assert.Contains(lines, l => l.StreamLabel == EffectiveVersionResolver.TerminalLabel
            && l.Kind == WorkerCommandStreamKind.Stdout
            && l.Text.StartsWith($"{EffectiveVersionScenario.ProducerName}: uses the published parent version {EffectiveVersionScenario.ParentVersion}", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Text.StartsWith($"{EffectiveVersionScenario.ConsumerName}: own version", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_unverifiable_package_is_a_red_terminal_line()
    {
        await using var s = await EffectiveVersionScenario.CreateAsync(configureServices: PolicyHarness.AddFacades);
        s.Registry.Answers(EffectiveVersionScenario.PackageId, HttpStatusCode.Forbidden);
        var terminal = new JobTerminalBuffer();

        await using var scope = s.Ctx.CreateScope();
        using (TerminalSinkContext.Use(terminal))
            await scope.ServiceProvider.GetRequiredService<IWorkspacePushOperations>().GetPlanAsync(s.Ctx.WorkspaceId, s.Special, progress: new RecordingProgress());

        Assert.Contains(terminal.GetSnapshot(), l => l.Kind == WorkerCommandStreamKind.Stderr
            && l.Text.StartsWith($"{EffectiveVersionScenario.ProducerName}: cannot verify its packages", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_background_refresh_writes_nothing_to_a_job_terminal()
    {
        await using var s = await EffectiveVersionScenario.CreateAsync(configureServices: PolicyHarness.AddFacades);
        var terminal = new JobTerminalBuffer();

        using (TerminalSinkContext.Use(terminal))
            await s.ResolveAsync(bypassCache: false);

        Assert.Empty(terminal.GetSnapshot());
    }

    [Fact]
    public async Task A_second_push_on_the_same_commit_reuses_the_parent_version_and_only_asks_the_registry_again()
    {
        await using var s = await EffectiveVersionScenario.CreateAsync(configureServices: PolicyHarness.AddFacades);
        await using (var first = s.Ctx.CreateScope())
            await first.ServiceProvider.GetRequiredService<IWorkspacePushOperations>().GetPlanAsync(s.Ctx.WorkspaceId, s.Special);
        var branchPointCalls = s.BranchPointCalls;
        var lookups = s.Registry.Lookups;
        var steps = new RecordingProgress();

        await using var scope = s.Ctx.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IWorkspacePushOperations>().GetPlanAsync(s.Ctx.WorkspaceId, s.Special, progress: steps);

        Assert.Equal(branchPointCalls, s.BranchPointCalls);
        Assert.Equal(lookups + 1, s.Registry.Lookups);
        Assert.Equal(["Checking versions..."], steps.Messages);
        var state = await s.ReadStateAsync(s.ProducerLinkId);
        Assert.Equal((int)EffectiveVersionKind.InheritedVerified, state.EffectiveVersionKind);
        Assert.Equal(EffectiveVersionScenario.ParentVersion, state.EffectiveVersion);
    }

    [Fact]
    public async Task A_push_after_the_commit_moved_calculates_the_parent_version_again()
    {
        await using var s = await EffectiveVersionScenario.CreateAsync(configureServices: PolicyHarness.AddFacades);
        await s.ResolveAsync(bypassCache: true);
        // The stored decision belongs to another commit now: nothing may be reused for this HEAD.
        await s.MutateStateAsync(s.ProducerLinkId, state => state.EffectiveVersionCommitSha = EffectiveVersionScenario.MovedSha);
        var branchPointCalls = s.BranchPointCalls;

        await using var scope = s.Ctx.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IWorkspacePushOperations>().GetPlanAsync(s.Ctx.WorkspaceId, s.Special);

        Assert.Equal(branchPointCalls + 1, s.BranchPointCalls);
    }

    [Theory]
    [InlineData(0, "No repositories to push.")]
    [InlineData(1, "No repositories to push. 1 branch without an upstream has no commits and needs no push.")]
    [InlineData(12, "No repositories to push. 12 branches without an upstream have no commits and need no push.")]
    public void Nothing_to_push_is_one_brief_sentence_whatever_the_number_of_branches(int noWorkCount, string expected)
    {
        Assert.Equal(expected, GrayMoon.App.Components.Pages.WorkspaceRepositories.NothingToPushMessage(noWorkCount));
    }

    /// <summary>Collects progress synchronously, in order (<see cref="Progress{T}"/> posts to the thread pool).</summary>
    private sealed class RecordingProgress : IProgress<OperationProgress>
    {
        private readonly object _lock = new();
        public List<string> Messages { get; } = [];
        public void Report(OperationProgress value)
        {
            lock (_lock) Messages.Add(value.Message);
        }
    }

    [Fact]
    public async Task Bulk_push_plan_bypasses_the_registry_cache()
    {
        await using var s = await EffectiveVersionScenario.CreateAsync(configureServices: PolicyHarness.AddFacades);
        await s.ResolveAsync(bypassCache: false);
        var before = s.Registry.Lookups;

        await using var scope = s.Ctx.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IWorkspacePushOperations>().GetPlanAsync(s.Ctx.WorkspaceId, s.Special);

        Assert.Equal(before + 1, s.Registry.Lookups);
    }

    [Fact]
    public async Task Update_stops_after_reading_projects_and_before_any_file_or_commit_change()
    {
        await using var s = await EffectiveVersionScenario.CreateAsync(consumerReferenceVersion: "1.0.0", configureServices: PolicyHarness.AddFacades);
        s.Registry.TimesOut(EffectiveVersionScenario.PackageId);
        // The project scan answers without projects, so the seeded graph is left as it is.
        s.Ctx.WorkerBridge.Respond("RefreshRepositoryProjects", data: null);
        var levelErrors = new List<string>();

        await using var scope = s.Ctx.CreateScope();
        var result = await scope.ServiceProvider.GetRequiredService<DependencyUpdateOrchestrator>().RunAsync(
            s.Ctx.WorkspaceId,
            s.Special,
            CancellationToken.None,
            progress: null,
            onRepoError: (_, _) => { },
            onLevelError: (_, message) => levelErrors.Add(message));

        Assert.False(result.Success);
        var stop = Assert.Single(levelErrors);
        Assert.Contains("did not answer in time", stop);
        Assert.Contains("Nothing was changed.", stop);
        Assert.DoesNotContain(s.Ctx.WorkerBridge.Calls, c => c.Command is "SyncRepositoryDependencies" or "CommitDependencyUpdates" or "UpdateFileVersions" or "StageAndCommit");
    }
}
