using GrayMoon.App.Data;
using GrayMoon.App.Models;
using GrayMoon.Application.Features;
using GrayMoon.Application.Repositories.Policy;
using Microsoft.EntityFrameworkCore;
using static GrayMoon.App.Tests.Policy.Characterization.PolicyHarness;

namespace GrayMoon.App.Tests.Policy;

/// <summary>
/// The subject loader: which row a context's branch and tag are read from, for each
/// <see cref="RepositorySubjectSource"/>, and how a bulk and a single decision are made from it.
/// </summary>
public sealed class WorkspaceRepositoryOperationPolicyServiceTests
{
    [Fact]
    public async Task The_Workspace_reads_the_shared_link()
    {
        await using var ctx = await CreateAsync();
        var graph = await SeedGraphAsync(ctx);
        await MutateLinkAsync(ctx, graph.ProducerId, link => { link.CheckedOutTag = Tag; link.BranchName = null; });

        var subjects = await Policy(ctx).GetSubjectsAsync(ctx.WorkspaceId, await ctx.GetSpecialContextIdAsync());

        Assert.True(subjects[graph.ProducerId].IsOnTag);
        Assert.False(subjects[graph.ProducerId].IsFeatureContext);
        Assert.False(subjects[graph.ConsumerId].IsOnTag);
        Assert.Equal("feature/x", subjects[graph.ConsumerId].BranchName);
    }

    [Fact]
    public async Task A_Feature_reads_its_own_state_and_never_the_Workspace_tag()
    {
        await using var ctx = await CreateAsync();
        var graph = await SeedGraphAsync(ctx);
        await PinWorkspaceAsync(ctx, graph.ProducerId);
        var feature = await SeedFeatureAsync(ctx,
        [
            new FeatureRepo(graph.ProducerId, Branch: "feature/y"),
            new FeatureRepo(graph.ConsumerId, Tag: Tag),
        ]);

        var subjects = await Policy(ctx).GetSubjectsAsync(ctx.WorkspaceId, feature);

        Assert.False(subjects[graph.ProducerId].IsOnTag);
        Assert.Equal("feature/y", subjects[graph.ProducerId].BranchName);
        Assert.True(subjects[graph.ConsumerId].IsOnTag);
        Assert.All(subjects.Values, subject => Assert.True(subject.IsFeatureContext));
    }

    [Fact]
    public async Task A_Feature_repository_without_a_state_row_is_on_no_branch_and_no_tag()
    {
        await using var ctx = await CreateAsync();
        var graph = await SeedGraphAsync(ctx);
        await PinWorkspaceAsync(ctx, graph.ProducerId);
        var feature = await SeedFeatureAsync(ctx, [new FeatureRepo(graph.ProducerId, WithState: false)]);

        var context = (await Policy(ctx).GetSubjectsAsync(ctx.WorkspaceId, feature))[graph.ProducerId];
        var stateFirst = (await Policy(ctx).GetSubjectsAsync(ctx.WorkspaceId, feature, RepositorySubjectSource.ContextStateFirst))[graph.ProducerId];

        Assert.Null(context.CheckedOutTag);
        Assert.Null(context.BranchName);
        Assert.Null(stateFirst.CheckedOutTag);
    }

    [Fact]
    public async Task The_legacy_source_judges_a_Feature_by_the_Workspace_link()
    {
        await using var ctx = await CreateAsync();
        var graph = await SeedGraphAsync(ctx);
        await PinWorkspaceAsync(ctx, graph.ProducerId);
        var feature = await SeedFeatureAsync(ctx, [new FeatureRepo(graph.ProducerId, Branch: "feature/y")]);

        var subject = (await Policy(ctx).GetSubjectsAsync(ctx.WorkspaceId, feature, RepositorySubjectSource.LegacySharedLink))[graph.ProducerId];

        Assert.Equal(Tag, subject.CheckedOutTag);
        Assert.True(subject.IsFeatureContext);
    }

    [Fact]
    public async Task State_first_prefers_the_Workspace_state_row_over_the_link()
    {
        await using var ctx = await CreateAsync();
        var graph = await SeedGraphAsync(ctx);
        var special = await ctx.GetSpecialContextIdAsync();
        // The link says tag, a state row of the special Workspace says branch: the two sources disagree on purpose.
        await MutateLinkAsync(ctx, graph.ProducerId, link => { link.CheckedOutTag = Tag; link.BranchName = null; });
        await AddStateRowAsync(ctx, special, graph.ProducerId, branch: "main", tag: null);

        var policy = Policy(ctx);
        var stateFirst = (await policy.GetSubjectsAsync(ctx.WorkspaceId, special, RepositorySubjectSource.ContextStateFirst))[graph.ProducerId];
        var context = (await policy.GetSubjectsAsync(ctx.WorkspaceId, special))[graph.ProducerId];
        var withoutRow = (await policy.GetSubjectsAsync(ctx.WorkspaceId, special, RepositorySubjectSource.ContextStateFirst))[graph.ConsumerId];

        Assert.False(stateFirst.IsOnTag);
        Assert.Equal("main", stateFirst.BranchName);
        Assert.True(context.IsOnTag);
        Assert.Equal("feature/x", withoutRow.BranchName);
    }

    [Fact]
    public async Task The_Workspace_repository_is_marked_and_follows_the_same_tag_rule()
    {
        await using var ctx = await CreateAsync();
        var graph = await SeedGraphAsync(ctx);
        await MutateLinkAsync(ctx, graph.ConsumerId, link =>
        {
            link.Role = WorkspaceRepositoryRole.Workspace;
            link.CheckedOutTag = Tag;
            link.BranchName = null;
        });
        var special = await ctx.GetSpecialContextIdAsync();

        var subjects = await Policy(ctx).GetSubjectsAsync(ctx.WorkspaceId, special);
        var decision = await Policy(ctx).EvaluateAsync(ctx.WorkspaceId, special, RepositoryOperation.Push, graph.ConsumerId);

        Assert.True(subjects[graph.ConsumerId].IsWorkspaceRepository);
        Assert.False(subjects[graph.ProducerId].IsWorkspaceRepository);
        Assert.Equal(RepositoryOperationOutcome.Refused, decision.Outcome);
    }

    [Fact]
    public async Task Partition_splits_the_Workspace_and_names_what_it_left_out()
    {
        await using var ctx = await CreateAsync();
        var graph = await SeedGraphAsync(ctx);
        await PinWorkspaceAsync(ctx, graph.ProducerId);
        var special = await ctx.GetSpecialContextIdAsync();

        var push = await Policy(ctx).PartitionAsync(ctx.WorkspaceId, special, RepositoryOperation.Push);
        var checkout = await Policy(ctx).PartitionAsync(ctx.WorkspaceId, special, RepositoryOperation.ManualCheckout);

        Assert.Equal([graph.ConsumerId], push.Eligible);
        var skip = Assert.Single(push.Skipped);
        Assert.Equal(graph.ProducerId, skip.RepositoryId);
        Assert.Equal(ProducerName, skip.RepositoryName);
        Assert.Equal(RepositoryOperationReason.OnTag, skip.Reason);
        Assert.Equal(RepositoryOperationPolicy.OnTagPushMessage, skip.Message);
        Assert.Equal(2, checkout.Eligible.Count);
        Assert.Empty(checkout.Skipped);
    }

    [Fact]
    public async Task Partition_keeps_the_order_of_the_ids_it_was_given_and_drops_unknown_ones()
    {
        await using var ctx = await CreateAsync();
        var graph = await SeedGraphAsync(ctx);
        var special = await ctx.GetSpecialContextIdAsync();
        var first = Math.Max(graph.ProducerId, graph.ConsumerId);
        var second = Math.Min(graph.ProducerId, graph.ConsumerId);

        var result = await Policy(ctx).PartitionAsync(
            ctx.WorkspaceId, special, RepositoryOperation.Push, [first, 987654, second, first]);

        Assert.Equal([first, second], result.Eligible);
        Assert.Empty(result.Skipped);
    }

    [Fact]
    public async Task Partition_of_no_ids_is_empty_and_of_null_is_every_repository()
    {
        await using var ctx = await CreateAsync();
        await SeedGraphAsync(ctx);
        var special = await ctx.GetSpecialContextIdAsync();

        var none = await Policy(ctx).PartitionAsync(ctx.WorkspaceId, special, RepositoryOperation.Push, []);
        var all = await Policy(ctx).PartitionAsync(ctx.WorkspaceId, special, RepositoryOperation.Push, null);

        Assert.Empty(none.Eligible);
        Assert.Equal(2, all.Eligible.Count);
    }

    [Fact]
    public async Task A_single_decision_is_a_refusal_with_the_message_and_never_a_skip()
    {
        await using var ctx = await CreateAsync();
        var graph = await SeedGraphAsync(ctx);
        await PinWorkspaceAsync(ctx, graph.ProducerId);
        var special = await ctx.GetSpecialContextIdAsync();

        var refused = await Policy(ctx).EvaluateAsync(ctx.WorkspaceId, special, RepositoryOperation.Push, graph.ProducerId);
        var allowed = await Policy(ctx).EvaluateAsync(ctx.WorkspaceId, special, RepositoryOperation.Push, graph.ConsumerId);

        Assert.Equal(RepositoryOperationOutcome.Refused, refused.Outcome);
        Assert.Equal(RepositoryOperationPolicy.OnTagPushMessage, refused.Message);
        Assert.True(allowed.IsAllowed);
    }

    [Fact]
    public async Task A_repository_outside_the_Workspace_and_an_unknown_context_have_nothing_to_object_with()
    {
        await using var ctx = await CreateAsync();
        var graph = await SeedGraphAsync(ctx);
        await PinWorkspaceAsync(ctx, graph.ProducerId);
        var special = await ctx.GetSpecialContextIdAsync();
        var unknownContext = new WorkspaceFeatureContextId(987654);

        var outside = await Policy(ctx).EvaluateAsync(ctx.WorkspaceId, special, RepositoryOperation.Push, 987654);
        var inUnknownContext = await Policy(ctx).EvaluateAsync(ctx.WorkspaceId, unknownContext, RepositoryOperation.Push, graph.ProducerId);
        var otherWorkspace = await Policy(ctx).GetSubjectsAsync(987654, special);

        Assert.True(outside.IsAllowed);
        // An unknown context is not the Workspace: it has no state rows, so it never borrows the Workspace tag.
        Assert.True(inUnknownContext.IsAllowed);
        Assert.Empty(otherWorkspace);
    }

    private static IWorkspaceRepositoryOperationPolicy Policy(SyncStateTestContext ctx) =>
        ctx.Resolve<IWorkspaceRepositoryOperationPolicy>();

    private static async Task AddStateRowAsync(
        SyncStateTestContext ctx, WorkspaceFeatureContextId contextId, int repositoryId, string? branch, string? tag)
    {
        await using var db = await ctx.Resolve<IDbContextFactory<AppDbContext>>().CreateDbContextAsync();
        var link = await db.WorkspaceRepositories.SingleAsync(l => l.WorkspaceId == ctx.WorkspaceId && l.RepositoryId == repositoryId);
        var state = await db.WorkspaceRepositoryContextStates.SingleOrDefaultAsync(s =>
            s.WorkspaceFeatureContextId == contextId.Value && s.WorkspaceRepositoryId == link.WorkspaceRepositoryId);
        if (state is null)
        {
            state = new WorkspaceRepositoryContextState
            {
                WorkspaceFeatureContextId = contextId.Value,
                WorkspaceRepositoryId = link.WorkspaceRepositoryId,
            };
            db.WorkspaceRepositoryContextStates.Add(state);
        }

        state.BranchName = branch;
        state.CheckedOutTag = tag;
        await db.SaveChangesAsync();
    }
}
