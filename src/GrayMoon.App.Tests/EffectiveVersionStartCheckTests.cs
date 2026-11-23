using System.Net;
using GrayMoon.App.Services.Orchestration;
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
