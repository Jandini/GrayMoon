using GrayMoon.Abstractions.Notifications;
using GrayMoon.Abstractions.Worker;
using GrayMoon.App.Data;
using GrayMoon.App.Models;
using GrayMoon.App.Models.Api;
using GrayMoon.Application.Features;
using GrayMoon.Application.Repositories.Policy;
using GrayMoon.Common.Git;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static GrayMoon.App.Tests.Policy.Characterization.PolicyHarness;

namespace GrayMoon.App.Tests.Policy.Characterization;

/// <summary>
/// Entry points that used to send a mutating Worker command for a tag-pinned repository because only the page checked
/// the tag. Each test pins the seeded repository in the special Workspace. Pull, Update Branch from Default and a
/// single Return to Default are now refused or skipped by the service. The others stay possible on a tag: they are how
/// a repository leaves a tag (checkout, new branch), or they do not depend on the checkout.
/// </summary>
public sealed class ServiceGapCharacterizationTests
{
    [Fact]
    public async Task Pull_of_a_tag_pinned_repository_is_refused_and_a_level_pull_leaves_it_out()
    {
        await using var ctx = await PinnedAsync();
        var special = await ctx.GetSpecialContextIdAsync();

        await using var scope = ctx.CreateScope();
        var sync = scope.ServiceProvider.GetRequiredService<IWorkspaceSyncOperations>();
        var single = await sync.PullAsync(ctx.WorkspaceId, special, ctx.RepositoryId, null, CancellationToken.None);
        var level = await sync.PullLevelAsync(ctx.WorkspaceId, special, [ctx.RepositoryId], null, CancellationToken.None);

        Assert.False(single.Success);
        Assert.Equal(RepositoryOperationPolicy.OnTagMessage, single.RepoErrors![ctx.RepositoryId]);
        Assert.True(level.Success);
        Assert.Equal([ctx.RepositoryId], level.Skipped.Select(skip => skip.RepositoryId));
        Assert.Empty(Calls(ctx, "CommitSyncRepository"));
    }

    [Fact]
    public async Task Bulk_create_branch_is_allowed_on_a_tag()
    {
        await using var ctx = await PinnedAsync();
        ctx.WorkerBridge.Respond("CreateBranch", new CreateBranchResponse { Success = true, CurrentBranch = "feature/prep", Branch = "feature/prep" });
        var special = await ctx.GetSpecialContextIdAsync();

        await using (var scope = ctx.CreateScope())
        {
            var errors = await scope.ServiceProvider.GetRequiredService<WorkspaceBranchHandler>()
                .CreateBranchesAsync(ctx.WorkspaceId, special, "feature/prep", "__default__", repositoryIds: null);
            Assert.Empty(errors);
        }

        Assert.Equal([ProducerName], RepositoryNames(ctx, "CreateBranch"));
        // The state write that follows takes the repository off its tag in the database.
        var link = await ctx.ReadLinkAsync();
        Assert.Null(link.CheckedOutTag);
        Assert.Equal("feature/prep", link.BranchName);
    }

    [Fact]
    public async Task Prepare_workspace_with_null_repository_ids_is_allowed_on_a_tag()
    {
        await using var ctx = await PinnedAsync();
        ctx.WorkerBridge.Respond("CreateBranch", new CreateBranchResponse { Success = true, CurrentBranch = "feature/prep", Branch = "feature/prep" });
        var special = await ctx.GetSpecialContextIdAsync();

        await using var scope = ctx.CreateScope();
        var result = await scope.ServiceProvider.GetRequiredService<IWorkspacePreparationOperations>().PrepareAsync(
            ctx.WorkspaceId,
            special,
            "feature/prep",
            "__default__",
            repositoryIds: null,
            updateDependencies: false,
            pushChanges: false,
            commitMessage: null,
            progress: null,
            setRepositoryError: (_, _) => { },
            setLevelError: (_, _) => { },
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal([ProducerName], RepositoryNames(ctx, "CreateBranch"));
    }

    [Fact]
    public async Task Workspace_wide_checkout_is_allowed_on_a_tag()
    {
        await using var ctx = await PinnedAsync();
        ctx.WorkerBridge.Respond("CheckoutBranch", new CheckoutBranchResponse { Success = true, CurrentBranch = "main" });
        var special = await ctx.GetSpecialContextIdAsync();

        await using (var scope = ctx.CreateScope())
        {
            var result = await scope.ServiceProvider.GetRequiredService<WorkspaceBranchHandler>().CheckoutBranchForWorkspaceAsync(
                ctx.WorkspaceId,
                special,
                new Dictionary<int, int> { [ctx.RepositoryId] = ctx.WorkspaceRepositoryId },
                "main",
                reportProgress: null,
                CancellationToken.None);
            Assert.Equal(1, result.SuccessCount);
        }

        Assert.Equal([ProducerName], RepositoryNames(ctx, "CheckoutBranch"));
        Assert.Null((await ctx.ReadLinkAsync()).CheckedOutTag);
    }

    [Fact]
    public async Task Git_changes_commit_is_allowed_on_a_tag()
    {
        await using var ctx = await PinnedAsync();
        var special = await ctx.GetSpecialContextIdAsync();

        await using var scope = ctx.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IWorkspaceGitChangesOperations>()
            .CommitAsync(ctx.WorkspaceId, special, ctx.RepositoryId, "message", stageAllFirst: true, CancellationToken.None);

        Assert.Equal([ProducerName], RepositoryNames(ctx, "CommitGitChanges"));
    }

    [Fact]
    public async Task Git_changes_stage_and_unstage_are_allowed_on_a_tag()
    {
        await using var ctx = await PinnedAsync();
        var special = await ctx.GetSpecialContextIdAsync();

        await using var scope = ctx.CreateScope();
        var operations = scope.ServiceProvider.GetRequiredService<IWorkspaceGitChangesOperations>();
        await operations.StageAsync(ctx.WorkspaceId, special, ctx.RepositoryId, GitChangeOperationScope.Repository, [], CancellationToken.None);
        await operations.UnstageAsync(ctx.WorkspaceId, special, ctx.RepositoryId, GitChangeOperationScope.Repository, [], CancellationToken.None);

        Assert.Equal([ProducerName], RepositoryNames(ctx, "StageGitChanges"));
        Assert.Equal([ProducerName], RepositoryNames(ctx, "UnstageGitChanges"));
    }

    [Fact]
    public async Task Update_branch_from_default_of_a_tag_pinned_repository_is_refused()
    {
        await using var ctx = await PinnedAsync();
        var special = await ctx.GetSpecialContextIdAsync();

        await using var scope = ctx.CreateScope();
        var outcome = await scope.ServiceProvider.GetRequiredService<IWorkspaceBranchOperations>()
            .UpdateBranchFromDefaultAsync(ctx.WorkspaceId, special, ctx.RepositoryId);

        Assert.Equal(400, outcome.StatusCode);
        Assert.Equal(RepositoryOperationPolicy.OnTagMessage, outcome.ErrorText);
        Assert.Empty(Calls(ctx, "UpdateBranchFromDefault"));
    }

    [Fact]
    public async Task Branch_checkout_of_a_tag_pinned_repository_is_not_refused_and_takes_it_off_the_tag()
    {
        await using var ctx = await PinnedAsync();
        ctx.WorkerBridge.Respond("CheckoutBranch", new CheckoutBranchResponse { Success = true, CurrentBranch = "main" });
        var special = await ctx.GetSpecialContextIdAsync();

        await using (var scope = ctx.CreateScope())
        {
            var outcome = await scope.ServiceProvider.GetRequiredService<IWorkspaceBranchOperations>()
                .CheckoutAsync(ctx.WorkspaceId, special, ctx.RepositoryId, "main", isTag: false);
            Assert.True(outcome.IsSuccessStatus);
        }

        Assert.Single(Calls(ctx, "CheckoutBranch"));
        var link = await ctx.ReadLinkAsync();
        Assert.Null(link.CheckedOutTag);
        Assert.Equal("main", link.BranchName);
    }

    [Fact]
    public async Task Create_branch_is_allowed_on_a_tag()
    {
        await using var ctx = await PinnedAsync();
        ctx.WorkerBridge.Respond("CreateBranch", new CreateBranchResponse { Success = true, CurrentBranch = "topic", Branch = "topic" });
        var special = await ctx.GetSpecialContextIdAsync();

        await using var scope = ctx.CreateScope();
        var outcome = await scope.ServiceProvider.GetRequiredService<IWorkspaceBranchOperations>()
            .CreateBranchAsync(ctx.WorkspaceId, special, ctx.RepositoryId, "topic", "main");

        Assert.True(outcome.IsSuccessStatus);
        Assert.Single(Calls(ctx, "CreateBranch"));
    }

    [Fact]
    public async Task Set_upstream_is_allowed_on_a_tag()
    {
        await using var ctx = await PinnedAsync();
        var special = await ctx.GetSpecialContextIdAsync();

        await using var scope = ctx.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IWorkspaceBranchOperations>()
            .SetUpstreamAsync(ctx.WorkspaceId, special, ctx.RepositoryId, "feature/x");

        Assert.Single(Calls(ctx, "SetUpstreamBranch"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Delete_branch_is_allowed_on_a_tag(bool isRemote)
    {
        await using var ctx = await PinnedAsync();
        ctx.WorkerBridge.Respond("DeleteBranch", new { success = true });
        var special = await ctx.GetSpecialContextIdAsync();

        await using var scope = ctx.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IWorkspaceBranchOperations>()
            .DeleteBranchAsync(ctx.WorkspaceId, special, ctx.RepositoryId, "feature/x", isRemote, force: false);

        Assert.Single(Calls(ctx, "DeleteBranch"));
    }

    [Fact]
    public async Task Single_return_to_default_of_a_tag_pinned_repository_is_refused()
    {
        await using var ctx = await PinnedAsync();
        ctx.WorkerBridge.Respond("ReturnToDefaultBranch", new ReturnToDefaultBranchResponse
        {
            Success = true,
            CurrentBranch = "main",
            DefaultBranch = "main",
            State = new RepositoryStateSnapshot { BranchName = "main", IdentityProbed = true },
        });
        var special = await ctx.GetSpecialContextIdAsync();

        await using var scope = ctx.CreateScope();
        var outcome = await scope.ServiceProvider.GetRequiredService<IWorkspaceBranchOperations>().ReturnToDefaultAsync(
            ctx.WorkspaceId, special, ctx.RepositoryId, "feature/x", deleteRemoteBranch: false, allowForceDeleteLocalBranch: false);

        Assert.Equal(400, outcome.StatusCode);
        Assert.Equal(RepositoryOperationPolicy.OnTagMessage, outcome.ErrorText);
        Assert.Empty(Calls(ctx, "ReturnToDefaultBranch"));
    }

    /// <summary>The one branch read that does filter: a tag-pinned link takes no part in the common-branch intersection.</summary>
    [Fact]
    public async Task Common_branches_leave_the_tag_pinned_repository_out_of_the_intersection()
    {
        await using var ctx = await CreateAsync();
        var graph = await SeedGraphAsync(ctx);
        await using (var db = await ctx.Resolve<IDbContextFactory<AppDbContext>>().CreateDbContextAsync())
        {
            var links = await db.WorkspaceRepositories.Where(l => l.WorkspaceId == ctx.WorkspaceId).ToListAsync();
            db.RepositoryBranches.RemoveRange(db.RepositoryBranches);
            foreach (var link in links)
            {
                var own = link.RepositoryId == graph.ProducerId ? "only-producer" : "only-consumer";
                db.RepositoryBranches.Add(new RepositoryBranch { WorkspaceRepositoryId = link.WorkspaceRepositoryId, BranchName = "main", IsDefault = true });
                db.RepositoryBranches.Add(new RepositoryBranch { WorkspaceRepositoryId = link.WorkspaceRepositoryId, BranchName = own });
            }

            await db.SaveChangesAsync();
        }

        var before = await CommonLocalAsync(ctx);
        await PinWorkspaceAsync(ctx, graph.ProducerId);
        var after = await CommonLocalAsync(ctx);

        // The shared default branch is removed from the result, so two repositories with nothing else in common have no common branch.
        Assert.Empty(before);
        Assert.Equal(["only-consumer"], after);
    }

    private static async Task<List<string>> CommonLocalAsync(SyncStateTestContext ctx)
    {
        await using var scope = ctx.CreateScope();
        var outcome = await scope.ServiceProvider.GetRequiredService<IWorkspaceBranchOperations>().GetCommonBranchesAsync(ctx.WorkspaceId);
        var body = Assert.IsType<CommonBranchesApiResult>(outcome.Body);
        return (body.CommonLocalBranchNames ?? []).Order(StringComparer.Ordinal).ToList();
    }

    /// <summary>The seeded repository, checked out on a tag in the special Workspace.</summary>
    private static async Task<SyncStateTestContext> PinnedAsync()
    {
        var ctx = await CreateAsync();
        await PinWorkspaceAsync(ctx, ctx.RepositoryId);
        return ctx;
    }
}
