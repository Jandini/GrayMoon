using GrayMoon.Abstractions.Notifications;
using GrayMoon.Abstractions.Worker;
using GrayMoon.App.Data;
using GrayMoon.App.Models;
using GrayMoon.App.Models.Api;
using GrayMoon.Application.Features;
using GrayMoon.Common.Git;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static GrayMoon.App.Tests.Policy.Characterization.PolicyHarness;

namespace GrayMoon.App.Tests.Policy.Characterization;

/// <summary>
/// Phase 0A, finding F5: entry points that send a mutating Worker command for a tag-pinned repository because no
/// service checks the tag (the only guard today is in the page that offers the action). Each test pins the seeded
/// repository in the special Workspace and records that the command still reaches the Worker. Also the six write
/// actions of <see cref="IWorkspaceBranchOperations"/>, none of which refuses a tag-pinned repository.
/// </summary>
public sealed class ServiceGapCharacterizationTests
{
    [Fact]
    public async Task Pull_has_no_service_side_tag_check_today()
    {
        await using var ctx = await PinnedAsync();
        var special = await ctx.GetSpecialContextIdAsync();

        await using var scope = ctx.CreateScope();
        var sync = scope.ServiceProvider.GetRequiredService<IWorkspaceSyncOperations>();
        await sync.PullAsync(ctx.WorkspaceId, special, ctx.RepositoryId, null, CancellationToken.None);
        await sync.PullLevelAsync(ctx.WorkspaceId, special, [ctx.RepositoryId], null, CancellationToken.None);

        Assert.Equal(2, Calls(ctx, "CommitSyncRepository").Count);
    }

    [Fact]
    public async Task Bulk_create_branch_has_no_service_side_tag_check_today()
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
    public async Task Prepare_workspace_with_null_repository_ids_has_no_service_side_tag_check_today()
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
    public async Task Workspace_wide_checkout_has_no_service_side_tag_check_today()
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
    public async Task Git_changes_commit_has_no_service_side_tag_check_today()
    {
        await using var ctx = await PinnedAsync();
        var special = await ctx.GetSpecialContextIdAsync();

        await using var scope = ctx.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IWorkspaceGitChangesOperations>()
            .CommitAsync(ctx.WorkspaceId, special, ctx.RepositoryId, "message", stageAllFirst: true, CancellationToken.None);

        Assert.Equal([ProducerName], RepositoryNames(ctx, "CommitGitChanges"));
    }

    [Fact]
    public async Task Git_changes_stage_and_unstage_have_no_service_side_tag_check_today()
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
    public async Task Update_branch_from_default_has_no_service_side_tag_check_today()
    {
        await using var ctx = await PinnedAsync();
        var special = await ctx.GetSpecialContextIdAsync();

        await using var scope = ctx.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IWorkspaceBranchOperations>()
            .UpdateBranchFromDefaultAsync(ctx.WorkspaceId, special, ctx.RepositoryId);

        Assert.Equal([ProducerName], RepositoryNames(ctx, "UpdateBranchFromDefault"));
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
    public async Task Create_branch_on_a_tag_pinned_repository_has_no_service_side_tag_check_today()
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
    public async Task Set_upstream_on_a_tag_pinned_repository_has_no_service_side_tag_check_today()
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
    public async Task Delete_branch_on_a_tag_pinned_repository_has_no_service_side_tag_check_today(bool isRemote)
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
    public async Task Single_return_to_default_on_a_tag_pinned_repository_has_no_service_side_tag_check_today()
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
        await scope.ServiceProvider.GetRequiredService<IWorkspaceBranchOperations>().ReturnToDefaultAsync(
            ctx.WorkspaceId, special, ctx.RepositoryId, "feature/x", deleteRemoteBranch: false, allowForceDeleteLocalBranch: false);

        Assert.Single(Calls(ctx, "ReturnToDefaultBranch"));
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
