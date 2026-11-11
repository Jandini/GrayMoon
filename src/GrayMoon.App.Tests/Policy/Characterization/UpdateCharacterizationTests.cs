using GrayMoon.App.Models;
using GrayMoon.Application.Features;
using Microsoft.Extensions.DependencyInjection;
using static GrayMoon.App.Tests.Policy.Characterization.PolicyHarness;

namespace GrayMoon.App.Tests.Policy.Characterization;

/// <summary>
/// Phase 0A: what update, commit and restore do today with a tag-pinned repository. Update and commit read the tag
/// from the shared Workspace link whatever the context (F6); restore reads it per context.
/// </summary>
public sealed class UpdateCharacterizationTests
{
    [Fact]
    public async Task Workspace_update_plan_lists_the_stale_consumer_and_leaves_it_out_once_it_is_on_a_tag()
    {
        await using var ctx = await CreateAsync();
        await ctx.UseDotNetDependencyProfileAsync();
        var graph = await SeedGraphAsync(ctx);
        var special = await ctx.GetSpecialContextIdAsync();

        var before = await UpdatePlanAsync(ctx, special);
        await PinWorkspaceAsync(ctx, graph.ConsumerId);
        var after = await UpdatePlanAsync(ctx, special);

        Assert.Equal([graph.ConsumerId], before);
        Assert.Empty(after);
    }

    [Fact]
    public async Task Workspace_update_plan_does_not_look_at_the_repository_role()
    {
        await using var ctx = await CreateAsync();
        await ctx.UseDotNetDependencyProfileAsync();
        var graph = await SeedGraphAsync(ctx);
        await MutateLinkAsync(ctx, graph.ConsumerId, link => link.Role = WorkspaceRepositoryRole.Workspace);

        var plan = await UpdatePlanAsync(ctx, await ctx.GetSpecialContextIdAsync());

        Assert.Equal([graph.ConsumerId], plan);
    }

    [Fact]
    public async Task Sync_dependencies_sends_nothing_to_the_Worker_for_a_tag_pinned_repository()
    {
        await using var ctx = await CreateAsync();
        await ctx.UseDotNetDependencyProfileAsync();
        var graph = await SeedGraphAsync(ctx);
        await PinWorkspaceAsync(ctx, graph.ConsumerId);
        var special = await ctx.GetSpecialContextIdAsync();

        await using var scope = ctx.CreateScope();
        var synced = await scope.ServiceProvider.GetRequiredService<WorkspaceGitService>().SyncDependenciesAsync(ctx.WorkspaceId, special);

        Assert.Empty(synced);
        Assert.Empty(Calls(ctx, "SyncRepositoryDependencies"));
    }

    [Fact]
    public async Task Sync_dependencies_reaches_the_Worker_for_a_branch_repository()
    {
        await using var ctx = await CreateAsync();
        await ctx.UseDotNetDependencyProfileAsync();
        await SeedGraphAsync(ctx);
        var special = await ctx.GetSpecialContextIdAsync();

        await using var scope = ctx.CreateScope();
        await scope.ServiceProvider.GetRequiredService<WorkspaceGitService>().SyncDependenciesAsync(ctx.WorkspaceId, special);

        Assert.Equal([ConsumerName], RepositoryNames(ctx, "SyncRepositoryDependencies"));
    }

    [Fact]
    public async Task Single_repository_update_of_a_tag_pinned_repository_returns_silently_without_a_Worker_call()
    {
        await using var ctx = await CreateAsync();
        await ctx.UseDotNetDependencyProfileAsync();
        var graph = await SeedGraphAsync(ctx);
        await PinWorkspaceAsync(ctx, graph.ConsumerId);
        var special = await ctx.GetSpecialContextIdAsync();
        var errors = new List<string>();

        await using var scope = ctx.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IWorkspaceUpdateOperations>().UpdateSingleRepositoryAsync(
            ctx.WorkspaceId, special, graph.ConsumerId, onRepoError: (_, message) => errors.Add(message));

        Assert.Empty(errors);
        Assert.Empty(ctx.WorkerBridge.Calls);
    }

    [Fact]
    public async Task Commit_dependency_updates_drops_the_tag_pinned_repository_and_commits_the_branch_one()
    {
        await using var ctx = await CreateAsync();
        await ctx.UseDotNetDependencyProfileAsync();
        var graph = await SeedGraphAsync(ctx);
        await PinWorkspaceAsync(ctx, graph.ProducerId);
        var special = await ctx.GetSpecialContextIdAsync();
        ctx.WorkerBridge.Respond("StageAndCommit", new { success = true, committed = true });
        SyncDependenciesRepoPayload Payload(int id, string name, string path) =>
            new(id, name, 1, [new SyncDependenciesProjectUpdate(path, [(PackageId, "0.9.0", "1.0.0")])]);

        await using var scope = ctx.CreateScope();
        var results = await scope.ServiceProvider.GetRequiredService<WorkspaceGitService>().CommitDependencyUpdatesAsync(
            ctx.WorkspaceId,
            special,
            [Payload(graph.ProducerId, ProducerName, "src/Api/Acme.Api.csproj"), Payload(graph.ConsumerId, ConsumerName, "src/Web/Acme.Web.csproj")]);

        Assert.Equal([graph.ConsumerId], results.Select(r => r.RepoId));
        Assert.Equal([ConsumerName], RepositoryNames(ctx, "StageAndCommit"));
    }

    [Fact]
    public async Task Commit_file_paths_drops_the_tag_pinned_repository_and_commits_the_branch_one()
    {
        await using var ctx = await CreateAsync();
        var graph = await SeedGraphAsync(ctx);
        await PinWorkspaceAsync(ctx, graph.ProducerId);
        var special = await ctx.GetSpecialContextIdAsync();
        ctx.WorkerBridge.Respond("StageAndCommit", new { success = true, committed = true });

        await using var scope = ctx.CreateScope();
        var results = await scope.ServiceProvider.GetRequiredService<WorkspaceGitService>().CommitFilePathsAsync(
            ctx.WorkspaceId,
            special,
            [(graph.ProducerId, ProducerName, ["version.txt"]), (graph.ConsumerId, ConsumerName, ["version.txt"])]);

        Assert.Equal([graph.ConsumerId], results.Select(r => r.RepoId));
        Assert.Equal([ConsumerName], RepositoryNames(ctx, "StageAndCommit"));
    }

    /// <summary>F6: the update plan of a Feature is filtered by the Workspace link's tag, not the Feature's own checkout.</summary>
    [Fact]
    public async Task Feature_update_plan_leaves_out_a_repository_the_Workspace_has_on_a_tag_although_the_Feature_is_on_a_branch()
    {
        await using var ctx = await CreateAsync();
        await ctx.UseDotNetDependencyProfileAsync();
        var graph = await SeedGraphAsync(ctx);
        var feature = await SeedFeatureAsync(
            ctx,
            [new FeatureRepo(graph.ProducerId), new FeatureRepo(graph.ConsumerId)],
            copyGraphProjects: graph);

        var before = await UpdatePlanAsync(ctx, feature);
        await PinWorkspaceAsync(ctx, graph.ConsumerId);
        var after = await UpdatePlanAsync(ctx, feature);

        Assert.Equal([graph.ConsumerId], before);
        Assert.Empty(after);
    }

    /// <summary>F6, the other direction: a repository the Feature has on a tag is still planned for an update.</summary>
    [Fact]
    public async Task Feature_update_plan_includes_a_repository_the_Feature_has_on_a_tag_while_the_Workspace_is_on_a_branch()
    {
        await using var ctx = await CreateAsync();
        await ctx.UseDotNetDependencyProfileAsync();
        var graph = await SeedGraphAsync(ctx);
        var feature = await SeedFeatureAsync(
            ctx,
            [new FeatureRepo(graph.ProducerId), new FeatureRepo(graph.ConsumerId, Tag: Tag, PinnedTag: Tag)],
            copyGraphProjects: graph);

        var plan = await UpdatePlanAsync(ctx, feature);

        Assert.Equal([graph.ConsumerId], plan);
    }

    /// <summary>F6: the commit step in a Feature also reads the Workspace link.</summary>
    [Fact]
    public async Task Feature_commit_drops_a_repository_the_Workspace_has_on_a_tag_and_keeps_one_the_Feature_has_on_a_tag()
    {
        await using var ctx = await CreateAsync();
        var graph = await SeedGraphAsync(ctx);
        await PinWorkspaceAsync(ctx, graph.ProducerId);
        var feature = await SeedFeatureAsync(
            ctx,
            [new FeatureRepo(graph.ProducerId), new FeatureRepo(graph.ConsumerId, Tag: Tag, PinnedTag: Tag)]);
        ctx.WorkerBridge.Respond("StageAndCommit", new { success = true, committed = true });

        await using var scope = ctx.CreateScope();
        await scope.ServiceProvider.GetRequiredService<WorkspaceGitService>().CommitFilePathsAsync(
            ctx.WorkspaceId,
            feature,
            [(graph.ProducerId, ProducerName, ["version.txt"]), (graph.ConsumerId, ConsumerName, ["version.txt"])]);

        Assert.Equal([ConsumerName], RepositoryNames(ctx, "StageAndCommit"));
    }

    [Fact]
    public async Task Workspace_restore_skips_the_tag_pinned_repository()
    {
        await using var ctx = await CreateAsync();
        await ctx.UseDotNetDependencyProfileAsync();
        var graph = await SeedGraphAsync(ctx);
        ctx.WorkerBridge.Respond("DotnetRestore", new { success = true });
        await PinWorkspaceAsync(ctx, graph.ProducerId);
        var special = await ctx.GetSpecialContextIdAsync();

        await using var scope = ctx.CreateScope();
        var operations = scope.ServiceProvider.GetRequiredService<IWorkspaceUpdateOperations>();
        var all = await operations.RestorePackagesAsync(ctx.WorkspaceId, special, null, CancellationToken.None);
        var synced = await operations.RestoreSyncedPackagesAsync(
            ctx.WorkspaceId, special, new HashSet<int> { graph.ProducerId, graph.ConsumerId }, null, CancellationToken.None);

        Assert.Equal(1, all);
        Assert.Equal(1, synced);
        Assert.All(RepositoryNames(ctx, "DotnetRestore"), name => Assert.Equal(ConsumerName, name));
    }

    /// <summary>Restore is per context: the Feature's own tag counts, the Workspace's does not (and a missing state row is not pinned).</summary>
    [Fact]
    public async Task Feature_restore_skips_what_the_Feature_has_on_a_tag_and_restores_what_only_the_Workspace_has_on_a_tag()
    {
        await using var ctx = await CreateAsync();
        await ctx.UseDotNetDependencyProfileAsync();
        var graph = await SeedGraphAsync(ctx);
        ctx.WorkerBridge.Respond("DotnetRestore", new { success = true });
        await PinWorkspaceAsync(ctx, graph.ProducerId);
        var feature = await SeedFeatureAsync(
            ctx,
            [new FeatureRepo(graph.ProducerId, WithState: false), new FeatureRepo(graph.ConsumerId, Tag: Tag, PinnedTag: Tag)],
            copyGraphProjects: graph);

        await using var scope = ctx.CreateScope();
        var restored = await scope.ServiceProvider.GetRequiredService<IWorkspaceUpdateOperations>()
            .RestorePackagesAsync(ctx.WorkspaceId, feature, null, CancellationToken.None);

        Assert.Equal(1, restored);
        Assert.Equal([ProducerName], RepositoryNames(ctx, "DotnetRestore"));
    }

    [Fact]
    public async Task Default_branch_review_ignores_a_tag_pinned_repository_and_reports_one_on_its_default_branch()
    {
        await using var ctx = await CreateAsync();
        await ctx.UseDotNetDependencyProfileAsync();
        var graph = await SeedGraphAsync(ctx);
        await MutateLinkAsync(ctx, graph.ConsumerId, link => link.BranchName = "main");
        var special = await ctx.GetSpecialContextIdAsync();

        var before = await ReviewAsync(ctx, special);
        await PinWorkspaceAsync(ctx, graph.ConsumerId);
        var after = await ReviewAsync(ctx, special);

        Assert.Equal([graph.ConsumerId], before.OnDefaultBranchRepoIds);
        Assert.Equal([graph.ConsumerId], before.Targets.Select(t => t.RepositoryId));
        Assert.Empty(after.OnDefaultBranchRepoIds);
        Assert.Empty(after.Targets);
    }

    private static async Task<List<int>> UpdatePlanAsync(SyncStateTestContext ctx, WorkspaceFeatureContextId contextId)
    {
        await using var scope = ctx.CreateScope();
        var (payload, _) = await scope.ServiceProvider.GetRequiredService<IWorkspaceUpdateOperations>()
            .GetUpdatePlanAsync(ctx.WorkspaceId, contextId);
        return payload.Select(p => p.RepoId).ToList();
    }

    private static async Task<UpdateAndPushDefaultBranchReview> ReviewAsync(SyncStateTestContext ctx, WorkspaceFeatureContextId contextId)
    {
        await using var scope = ctx.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IWorkspaceUpdateAndPushOperations>()
            .GetDefaultBranchReviewAsync(ctx.WorkspaceId, contextId, null, CancellationToken.None);
    }
}
