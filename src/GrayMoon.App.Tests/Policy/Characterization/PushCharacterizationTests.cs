using GrayMoon.App.Models;
using GrayMoon.Application.Features;
using Microsoft.Extensions.DependencyInjection;
using static GrayMoon.App.Tests.Policy.Characterization.PolicyHarness;

namespace GrayMoon.App.Tests.Policy.Characterization;

/// <summary>
/// Phase 0A: what push does today with a tag-pinned repository, in the special Workspace and in a Feature. The push
/// path reads the tag per context (the shared link for the Workspace, the Feature's own state for a Feature).
/// </summary>
public sealed class PushCharacterizationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Workspace_push_plan_lists_branch_repositories_and_leaves_out_the_tag_pinned_one(bool dotNetProfile)
    {
        await using var ctx = await CreateAsync();
        if (dotNetProfile)
            await ctx.UseDotNetDependencyProfileAsync();
        var graph = await SeedGraphAsync(ctx);
        var special = await ctx.GetSpecialContextIdAsync();

        var before = await PlanAsync(ctx, special);
        await PinWorkspaceAsync(ctx, graph.ProducerId);
        var after = await PlanAsync(ctx, special);

        Assert.Equal([graph.ProducerId, graph.ConsumerId], before.RepositoryIds.Order());
        Assert.Equal([graph.ConsumerId], after.RepositoryIds);
        Assert.True(after.HasUnpushed);
    }

    [Fact]
    public async Task Workspace_push_plan_with_only_a_tag_pinned_repository_has_nothing_to_push()
    {
        await using var ctx = await CreateAsync();
        await PinWorkspaceAsync(ctx, ctx.RepositoryId);

        var plan = await PlanAsync(ctx, await ctx.GetSpecialContextIdAsync());

        Assert.Empty(plan.RepositoryIds);
        Assert.False(plan.HasUnpushed);
    }

    [Fact]
    public async Task Workspace_push_plan_does_not_look_at_the_repository_role()
    {
        await using var ctx = await CreateAsync();
        var graph = await SeedGraphAsync(ctx);
        await MutateLinkAsync(ctx, graph.ProducerId, link => link.Role = WorkspaceRepositoryRole.Workspace);

        var plan = await PlanAsync(ctx, await ctx.GetSpecialContextIdAsync());

        Assert.Equal([graph.ProducerId, graph.ConsumerId], plan.RepositoryIds.Order());
    }

    [Fact]
    public async Task Feature_push_plan_reads_the_tag_from_the_Feature_state_not_from_the_Workspace_link()
    {
        await using var ctx = await CreateAsync();
        var graph = await SeedGraphAsync(ctx);
        // Workspace: producer on a tag, consumer on a branch. Feature: the other way round.
        await PinWorkspaceAsync(ctx, graph.ProducerId);
        var feature = await SeedFeatureAsync(ctx,
        [
            new FeatureRepo(graph.ProducerId, Branch: "feature/y"),
            new FeatureRepo(graph.ConsumerId, Tag: Tag, PinnedTag: Tag),
        ]);

        var plan = await PlanAsync(ctx, feature);

        Assert.Equal([graph.ProducerId], plan.RepositoryIds);
    }

    [Fact]
    public async Task Single_push_of_a_tag_pinned_Workspace_repository_is_refused_before_the_Worker_is_called()
    {
        await using var ctx = await CreateAsync();
        RespondPushCommands(ctx);
        await PinWorkspaceAsync(ctx, ctx.RepositoryId);

        var result = await PushSingleAsync(ctx, await ctx.GetSpecialContextIdAsync(), ctx.RepositoryId, "feature/x");

        Assert.False(result.Success);
        Assert.Equal("Repository is pinned to a tag. Checkout a branch before pushing.", result.Error);
        Assert.Empty(Calls(ctx, "PushRepository"));
    }

    [Fact]
    public async Task Single_push_of_a_branch_Workspace_repository_reaches_the_Worker()
    {
        await using var ctx = await CreateAsync();
        RespondPushCommands(ctx);

        var result = await PushSingleAsync(ctx, await ctx.GetSpecialContextIdAsync(), ctx.RepositoryId, "feature/x");

        Assert.True(result.Success, result.Error);
        Assert.Single(Calls(ctx, "PushRepository"));
    }

    [Fact]
    public async Task Single_push_in_a_Feature_on_a_tag_is_refused_even_when_the_Workspace_is_on_a_branch()
    {
        await using var ctx = await CreateAsync();
        RespondPushCommands(ctx);
        var feature = await SeedFeatureAsync(ctx, [new FeatureRepo(ctx.RepositoryId, Tag: Tag, PinnedTag: Tag)]);

        var result = await PushSingleAsync(ctx, feature, ctx.RepositoryId, "feature/y");

        Assert.False(result.Success);
        Assert.Equal("Repository is pinned to a tag. Checkout a branch before pushing.", result.Error);
        Assert.Empty(Calls(ctx, "PushRepository"));
    }

    [Fact]
    public async Task Single_push_in_a_Feature_on_a_branch_is_allowed_even_when_the_Workspace_is_on_a_tag()
    {
        await using var ctx = await CreateAsync();
        RespondPushCommands(ctx);
        await PinWorkspaceAsync(ctx, ctx.RepositoryId);
        var feature = await SeedFeatureAsync(ctx, [new FeatureRepo(ctx.RepositoryId, Branch: "feature/y")]);

        var result = await PushSingleAsync(ctx, feature, ctx.RepositoryId, "feature/y");

        Assert.True(result.Success, result.Error);
        Assert.Single(Calls(ctx, "PushRepository"));
    }

    [Fact]
    public async Task Repositories_needing_push_skip_the_tag_pinned_one_and_count_a_branch_never_pushed_upstream()
    {
        await using var ctx = await CreateAsync();
        var graph = await SeedGraphAsync(ctx);
        var special = await ctx.GetSpecialContextIdAsync();
        await MutateLinkAsync(ctx, graph.ConsumerId, link =>
        {
            link.OutgoingCommits = 0;
            link.BranchHasUpstream = false;
        });
        var all = new HashSet<int> { graph.ProducerId, graph.ConsumerId };

        var before = await NeedingPushAsync(ctx, special, all);
        await PinWorkspaceAsync(ctx, graph.ProducerId);
        var after = await NeedingPushAsync(ctx, special, all);

        Assert.Equal([graph.ProducerId, graph.ConsumerId], before.Order());
        Assert.Equal([graph.ConsumerId], after);
    }

    /// <summary>
    /// F7. A Feature repository with no state row is treated two ways today: the needs-push query skips it, while
    /// the push plan and the single push do not see it as pinned (the tag is never borrowed from the Workspace link).
    /// </summary>
    [Fact]
    public async Task Feature_repository_with_no_state_row_is_not_in_the_needs_push_set()
    {
        await using var ctx = await CreateAsync();
        var feature = await SeedFeatureAsync(ctx, [new FeatureRepo(ctx.RepositoryId, WithState: false)]);

        var needing = await NeedingPushAsync(ctx, feature, new HashSet<int> { ctx.RepositoryId });

        Assert.Empty(needing);
    }

    [Fact]
    public async Task Feature_repository_with_no_state_row_is_not_pinned_for_single_push_even_when_the_Workspace_is_on_a_tag()
    {
        await using var ctx = await CreateAsync();
        RespondPushCommands(ctx);
        await PinWorkspaceAsync(ctx, ctx.RepositoryId);
        var feature = await SeedFeatureAsync(ctx, [new FeatureRepo(ctx.RepositoryId, WithState: false)]);

        var result = await PushSingleAsync(ctx, feature, ctx.RepositoryId, "feature/y");

        Assert.NotEqual("Repository is pinned to a tag. Checkout a branch before pushing.", result.Error);
        Assert.Single(Calls(ctx, "PushRepository"));
    }

    [Fact]
    public async Task Feature_repository_with_no_state_row_is_left_out_of_the_push_plan()
    {
        await using var ctx = await CreateAsync();
        var feature = await SeedFeatureAsync(ctx, [new FeatureRepo(ctx.RepositoryId, WithState: false)]);

        var plan = await PlanAsync(ctx, feature);

        Assert.Empty(plan.RepositoryIds);
    }

    private static async Task<WorkspacePushPlan> PlanAsync(SyncStateTestContext ctx, WorkspaceFeatureContextId contextId)
    {
        await using var scope = ctx.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IWorkspacePushOperations>().GetPlanAsync(ctx.WorkspaceId, contextId);
    }

    private static async Task<IReadOnlySet<int>> NeedingPushAsync(SyncStateTestContext ctx, WorkspaceFeatureContextId contextId, IReadOnlySet<int> ids)
    {
        await using var scope = ctx.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IWorkspacePushOperations>()
            .GetRepositoryIdsNeedingPushAsync(ctx.WorkspaceId, contextId, ids);
    }

    private static async Task<OperationResult> PushSingleAsync(SyncStateTestContext ctx, WorkspaceFeatureContextId contextId, int repositoryId, string branch)
    {
        await using var scope = ctx.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IWorkspacePushOperations>()
            .PushSingleAsync(ctx.WorkspaceId, contextId, repositoryId, branch);
    }
}
