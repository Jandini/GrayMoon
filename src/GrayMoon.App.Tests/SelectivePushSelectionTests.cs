using GrayMoon.App.Models;
using GrayMoon.App.Repositories;
using Microsoft.Extensions.DependencyInjection;

namespace GrayMoon.App.Tests;

/// <summary>
/// Push selection (design 9.1, plan package F). On the old rule every branch without an upstream was pushed
/// (<c>OutgoingCommits=0, BranchHasUpstream=false</c> is in the bulk plan, red test 3); now a branch still on its
/// recorded branch point is left out when its packages are reused or nobody uses its version.
/// </summary>
public sealed class SelectivePushSelectionTests
{
    [Fact]
    public async Task An_inheriting_producer_and_a_consumer_without_consumers_are_not_pushed()
    {
        await using var s = await EffectiveVersionScenario.CreateAsync();
        await s.ResolveAsync();

        var needingPush = await NeedingPushAsync(s);
        var noWork = await NoWorkAsync(s);

        Assert.Empty(needingPush);
        Assert.Contains(noWork, line => line.Contains($"Skipped push of {EffectiveVersionScenario.ProducerName}: no commits, required packages verified at {EffectiveVersionScenario.ParentVersion}."));
        Assert.Contains(noWork, line => line.Contains($"Skipped push of {EffectiveVersionScenario.ConsumerName}: no commits, and no repository in this context uses its version."));
    }

    [Fact]
    public async Task A_producer_whose_package_is_missing_must_publish()
    {
        await using var s = await EffectiveVersionScenario.CreateAsync();
        s.Registry.Lists(EffectiveVersionScenario.PackageId, "1.0.0");
        await s.ResolveAsync();

        var needingPush = await NeedingPushAsync(s);

        Assert.Contains(s.ProducerId, needingPush);
        Assert.DoesNotContain(s.ConsumerId, needingPush);
    }

    [Fact]
    public async Task Outgoing_commits_are_always_pushed_even_on_an_inheriting_branch()
    {
        await using var s = await EffectiveVersionScenario.CreateAsync();
        await s.ResolveAsync();
        await s.MutateStateAsync(s.ProducerLinkId, state => state.OutgoingCommits = 1);
        await s.MutateStateAsync(s.ConsumerLinkId, state => { state.OutgoingCommits = 2; state.BranchHasUpstream = true; });

        var needingPush = await NeedingPushAsync(s);

        Assert.Contains(s.ProducerId, needingPush);
        Assert.Contains(s.ConsumerId, needingPush);
    }

    [Fact]
    public async Task A_branch_with_an_upstream_and_nothing_outgoing_is_not_pushed()
    {
        await using var s = await EffectiveVersionScenario.CreateAsync();
        await s.MutateStateAsync(s.ProducerLinkId, state => state.BranchHasUpstream = true);
        s.Registry.Lists(EffectiveVersionScenario.PackageId, "1.0.0");
        await s.ResolveAsync();

        Assert.DoesNotContain(s.ProducerId, await NeedingPushAsync(s));
    }

    [Fact]
    public async Task A_branch_without_a_recorded_branch_point_keeps_the_old_rule()
    {
        await using var s = await EffectiveVersionScenario.CreateAsync();
        await s.WithDbAsync(db =>
        {
            db.WorkspaceRepositoryBranchBaselines.RemoveRange(db.WorkspaceRepositoryBranchBaselines);
            return Task.CompletedTask;
        });
        await s.ResolveAsync();

        var needingPush = await NeedingPushAsync(s);

        Assert.Contains(s.ProducerId, needingPush);
        Assert.Contains(s.ConsumerId, needingPush);
    }

    [Fact]
    public async Task A_stale_inheritance_for_another_HEAD_is_not_evidence()
    {
        await using var s = await EffectiveVersionScenario.CreateAsync();
        await s.ResolveAsync();
        // HEAD moved (a commit that is already pushed) without a new resolution: the stored inheritance is void (I10).
        await s.MutateStateAsync(s.ProducerLinkId, state => state.HeadCommit = EffectiveVersionScenario.MovedSha);

        Assert.Contains(s.ProducerId, await NeedingPushAsync(s));
    }

    [Fact]
    public async Task An_unresolved_decision_falls_back_to_pushing()
    {
        await using var s = await EffectiveVersionScenario.CreateAsync();
        s.Registry.Answers(EffectiveVersionScenario.PackageId, System.Net.HttpStatusCode.Forbidden);
        await s.ResolveAsync();

        Assert.Contains(s.ProducerId, await NeedingPushAsync(s));
    }

    [Fact]
    public async Task A_repository_that_was_never_resolved_keeps_the_old_rule()
    {
        await using var s = await EffectiveVersionScenario.CreateAsync();

        var needingPush = await NeedingPushAsync(s);

        Assert.Contains(s.ProducerId, needingPush);
        Assert.Contains(s.ConsumerId, needingPush);
    }

    [Fact]
    public async Task A_version_file_consumer_makes_an_own_version_publish()
    {
        await using var s = await EffectiveVersionScenario.CreateAsync();
        // The consumer stamps the producer's version in a file; nothing references the producer's package any more.
        await s.WithDbAsync(async db =>
        {
            db.ProjectDependencies.RemoveRange(db.ProjectDependencies);
            var file = new WorkspaceFile { WorkspaceId = s.Ctx.WorkspaceId, RepositoryId = s.ConsumerId, FilePath = "version.props", FileName = "version.props" };
            db.WorkspaceFiles.Add(file);
            await db.SaveChangesAsync();
            db.WorkspaceFileVersionConfigs.Add(new WorkspaceFileVersionConfig { FileId = file.FileId, VersionPattern = $"API={{@{EffectiveVersionScenario.ProducerName}}}" });
        });
        await s.ResolveAsync();

        var producer = await s.ReadStateAsync(s.ProducerLinkId);
        Assert.Equal((int)EffectiveVersionKind.Own, producer.EffectiveVersionKind);
        Assert.Contains(s.ProducerId, await NeedingPushAsync(s));
    }

    [Fact]
    public async Task A_policy_protected_repository_is_left_out()
    {
        await using var s = await EffectiveVersionScenario.CreateAsync();
        await s.MutateStateAsync(s.ProducerLinkId, state => { state.CheckedOutTag = "v2.3.0"; state.OutgoingCommits = 5; });
        await s.ResolveAsync();

        Assert.DoesNotContain(s.ProducerId, await NeedingPushAsync(s));
    }

    [Fact]
    public async Task Feature_selection_reads_the_Feature_rows()
    {
        await using var s = await EffectiveVersionScenario.CreateAsync();
        var feature = await s.AddFeatureAsync();
        await s.ResolveAsync(feature);

        await using var scope = s.Ctx.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<WorkspaceRepository>();
        var featureNeeding = await repo.GetRepositoryIdsNeedingPushAsync(s.Ctx.WorkspaceId, feature.Value, new HashSet<int> { s.ProducerId, s.ConsumerId });
        var specialNeeding = await NeedingPushAsync(s);

        Assert.Empty(featureNeeding);
        // The special Workspace was never resolved: its own (old) rule still applies there.
        Assert.Contains(s.ProducerId, specialNeeding);
    }

    private static async Task<IReadOnlySet<int>> NeedingPushAsync(EffectiveVersionScenario s)
    {
        await using var scope = s.Ctx.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<WorkspaceRepository>()
            .GetRepositoryIdsNeedingPushAsync(s.Ctx.WorkspaceId, s.Special.Value, new HashSet<int> { s.ProducerId, s.ConsumerId });
    }

    private static async Task<IReadOnlyList<string>> NoWorkAsync(EffectiveVersionScenario s)
    {
        await using var scope = s.Ctx.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<WorkspaceRepository>()
            .GetPushNoWorkReasonsAsync(s.Ctx.WorkspaceId, s.Special.Value, new HashSet<int> { s.ProducerId, s.ConsumerId });
    }
}
