using GrayMoon.App.Data;
using GrayMoon.App.Models;
using GrayMoon.App.Services.Orchestration;
using GrayMoon.Application;
using GrayMoon.Application.Features;
using GrayMoon.Application.Repositories.Policy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static GrayMoon.App.Tests.Policy.Characterization.PolicyHarness;

namespace GrayMoon.App.Tests.Policy;

/// <summary>
/// Which row each path reads for the tag of a checkout. These tests fail when a path is moved to another
/// <see cref="RepositorySubjectSource"/> by accident.
/// </summary>
public sealed class RepositoryPolicySubjectSourceTests
{
    [Fact]
    public async Task The_Workspace_push_plan_reads_the_state_row_before_the_link()
    {
        await using var ctx = await CreateAsync();
        var graph = await SeedGraphAsync(ctx);
        var special = await ctx.GetSpecialContextIdAsync();
        await AddStateAsync(ctx, special, graph.ProducerId, branch: null, tag: Tag);

        await using var scope = ctx.CreateScope();
        var plan = await scope.ServiceProvider.GetRequiredService<IWorkspacePushOperations>().GetPlanAsync(ctx.WorkspaceId, special);

        Assert.Equal([graph.ConsumerId], plan.RepositoryIds);
    }

    [Fact]
    public async Task The_Workspace_push_plan_follows_a_state_row_on_a_branch_although_the_link_is_on_a_tag()
    {
        await using var ctx = await CreateAsync();
        var graph = await SeedGraphAsync(ctx);
        var special = await ctx.GetSpecialContextIdAsync();
        await MutateLinkAsync(ctx, graph.ProducerId, link =>
        {
            link.CheckedOutTag = Tag;
            link.BranchName = null;
        });
        await AddStateAsync(ctx, special, graph.ProducerId, branch: "feature/x", tag: null);

        await using var scope = ctx.CreateScope();
        var plan = await scope.ServiceProvider.GetRequiredService<IWorkspacePushOperations>().GetPlanAsync(ctx.WorkspaceId, special);

        Assert.Equal([graph.ProducerId, graph.ConsumerId], plan.RepositoryIds.Order());
    }

    [Fact]
    public async Task A_Feature_update_run_names_what_the_Feature_has_on_a_tag_and_not_what_the_Workspace_has()
    {
        await using var ctx = await CreateAsync();
        var graph = await SeedGraphAsync(ctx);
        var feature = await SeedFeatureAsync(
            ctx,
            [new FeatureRepo(graph.ProducerId), new FeatureRepo(graph.ConsumerId, Tag: Tag, PinnedTag: Tag)]);
        await PinWorkspaceAsync(ctx, graph.ProducerId);

        var result = await RunUpdateAsync(ctx, feature);

        var skip = Assert.Single(result.Skipped);
        Assert.Equal(graph.ConsumerId, skip.RepositoryId);
        Assert.Equal(RepositoryOperationReason.OnTag, skip.Reason);
    }

    private static async Task<DependencyUpdateRunResult> RunUpdateAsync(SyncStateTestContext ctx, WorkspaceFeatureContextId contextId)
    {
        await using var scope = ctx.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<DependencyUpdateOrchestrator>().RunAsync(
            ctx.WorkspaceId, contextId, CancellationToken.None, progress: null, onRepoError: (_, _) => { }, onLevelError: (_, _) => { });
    }

    private static async Task AddStateAsync(
        SyncStateTestContext ctx,
        WorkspaceFeatureContextId contextId,
        int repositoryId,
        string? branch,
        string? tag)
    {
        await using var db = await ctx.Resolve<IDbContextFactory<AppDbContext>>().CreateDbContextAsync();
        var link = await db.WorkspaceRepositories.SingleAsync(l => l.WorkspaceId == ctx.WorkspaceId && l.RepositoryId == repositoryId);
        db.WorkspaceRepositoryContextStates.Add(new WorkspaceRepositoryContextState
        {
            WorkspaceFeatureContextId = contextId.Value,
            WorkspaceRepositoryId = link.WorkspaceRepositoryId,
            BranchName = branch,
            CheckedOutTag = tag,
            GitVersion = link.GitVersion,
            DependencyLevel = link.DependencyLevel,
            OutgoingCommits = 1,
            BranchHasUpstream = true,
            SyncStatus = RepoSyncStatus.InSync,
        });
        await db.SaveChangesAsync();
    }
}
