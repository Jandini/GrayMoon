using GrayMoon.Abstractions.Worker;
using GrayMoon.App.Models.Api;
using GrayMoon.Application.Features;
using Microsoft.Extensions.DependencyInjection;
using static GrayMoon.App.Tests.Policy.Characterization.PolicyHarness;

namespace GrayMoon.App.Tests.Policy.Characterization;

/// <summary>
/// Phase 0A: undo push and return to default through <see cref="IWorkspaceSyncOperations"/> with a tag-pinned
/// repository. Both skip it; undo push reads the tag per context.
/// </summary>
public sealed class SyncCharacterizationTests
{
    [Fact]
    public async Task Workspace_undo_push_rewinds_branch_repositories_and_skips_the_tag_pinned_one()
    {
        await using var ctx = await CreateAsync();
        var graph = await SeedGraphAsync(ctx);
        await PinWorkspaceAsync(ctx, graph.ProducerId);
        // A tag checkout keeps no branch name; give it one so only the tag check can be what skips it.
        await MutateLinkAsync(ctx, graph.ProducerId, link => link.BranchName = "feature/x");
        var special = await ctx.GetSpecialContextIdAsync();

        await UndoPushAsync(ctx, special);

        Assert.Equal([ConsumerName], RepositoryNames(ctx, "UndoPush"));
    }

    [Fact]
    public async Task Feature_undo_push_reads_the_tag_from_the_Feature_state_not_from_the_Workspace_link()
    {
        await using var ctx = await CreateAsync();
        var graph = await SeedGraphAsync(ctx);
        // Workspace: producer on a tag. Feature: producer on a branch, consumer on a tag.
        await PinWorkspaceAsync(ctx, graph.ProducerId);
        var feature = await SeedFeatureAsync(ctx,
        [
            new FeatureRepo(graph.ProducerId, Branch: "feature/y"),
            new FeatureRepo(graph.ConsumerId, Tag: Tag, PinnedTag: Tag),
        ]);

        await UndoPushAsync(ctx, feature);

        Assert.Equal([ProducerName], RepositoryNames(ctx, "UndoPush"));
    }

    /// <summary>F7: with no state row the overlay yields no branch and no outgoing count, so undo push skips the repository.</summary>
    [Fact]
    public async Task Feature_undo_push_skips_a_repository_with_no_state_row()
    {
        await using var ctx = await CreateAsync();
        var feature = await SeedFeatureAsync(ctx, [new FeatureRepo(ctx.RepositoryId, WithState: false)]);

        await UndoPushAsync(ctx, feature);

        Assert.Empty(Calls(ctx, "UndoPush"));
    }

    [Fact]
    public async Task Return_to_default_analysis_reports_the_tag_pinned_repository_as_on_a_tag_and_not_blocking()
    {
        await using var ctx = await TagPinnedForReturnAsync();

        await using var scope = ctx.CreateScope();
        var plan = await scope.ServiceProvider.GetRequiredService<IWorkspaceSyncOperations>().AnalyzeReturnToDefaultAsync(
            ctx.WorkspaceId, await ctx.GetSpecialContextIdAsync(), [ctx.RepositoryId], null, CancellationToken.None);

        var repo = Assert.Single(plan.Repositories);
        Assert.True(repo.IsOnTag);
        Assert.Null(repo.BlockingReason);
        Assert.False(plan.HasBlockingRepositories);
        Assert.True(plan.CanProceedAutomatically);
    }

    [Fact]
    public async Task Return_to_default_execution_skips_the_tag_pinned_repository_and_succeeds()
    {
        await using var ctx = await TagPinnedForReturnAsync();

        await using var scope = ctx.CreateScope();
        var result = await scope.ServiceProvider.GetRequiredService<IWorkspaceSyncOperations>().ExecuteReturnToDefaultAsync(
            ctx.WorkspaceId,
            await ctx.GetSpecialContextIdAsync(),
            [ctx.RepositoryId],
            new ReturnToDefaultOptions(DeleteRemoteBranch: false, AllowForceDeleteLocalBranch: false, CloseOpenPullRequest: false),
            null,
            CancellationToken.None);

        Assert.True(result.Success, result.Error);
        Assert.Empty(Calls(ctx, "ReturnToDefaultBranch"));
        Assert.Equal(Tag, (await ctx.ReadLinkAsync()).CheckedOutTag);
    }

    [Fact]
    public async Task Unattended_return_to_default_completes_and_leaves_the_tag_pinned_repository_alone()
    {
        await using var ctx = await TagPinnedForReturnAsync();

        await using var scope = ctx.CreateScope();
        var result = await scope.ServiceProvider.GetRequiredService<IWorkspaceSyncOperations>().ReturnToDefaultAsync(
            ctx.WorkspaceId, await ctx.GetSpecialContextIdAsync(), [ctx.RepositoryId], null, CancellationToken.None);

        Assert.True(result.Completed);
        Assert.Null(result.AbortReason);
        Assert.Empty(Calls(ctx, "ReturnToDefaultBranch"));
    }

    [Fact]
    public async Task Return_to_default_batch_reports_the_tag_pinned_repository_completed_without_a_mutation()
    {
        await using var ctx = await TagPinnedForReturnAsync();

        await using var scope = ctx.CreateScope();
        var batch = await scope.ServiceProvider.GetRequiredService<IWorkspaceSyncOperations>().ReturnToDefaultBatchAsync(
            ctx.WorkspaceId, await ctx.GetSpecialContextIdAsync(), [ctx.RepositoryId], null, CancellationToken.None);

        var repo = Assert.Single(batch.Results);
        Assert.True(repo.Completed);
        Assert.False(repo.MutationAttempted);
        Assert.Empty(Calls(ctx, "ReturnToDefaultBranch"));
    }

    private static async Task UndoPushAsync(SyncStateTestContext ctx, WorkspaceFeatureContextId contextId)
    {
        await using var scope = ctx.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IWorkspaceSyncOperations>()
            .UndoPushAsync(ctx.WorkspaceId, contextId, keepChanges: true, null, CancellationToken.None);
    }

    private static async Task<SyncStateTestContext> TagPinnedForReturnAsync()
    {
        var ctx = await CreateAsync();
        ctx.WorkerBridge.Respond("RefreshBranches", new BranchesResponse
        {
            Success = true,
            LocalBranches = ["main"],
            RemoteBranches = ["origin/main"],
            CurrentBranch = null,
            DefaultBranch = "main",
            Tags = [Tag],
            CurrentTag = Tag,
            UpstreamProbed = false,
        });
        await PinWorkspaceAsync(ctx, ctx.RepositoryId);
        await ctx.MutateLinkAsync(link => link.DefaultBranchAheadCommits = 0);
        return ctx;
    }
}
