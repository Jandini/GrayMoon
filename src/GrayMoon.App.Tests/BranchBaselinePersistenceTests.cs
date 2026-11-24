using GrayMoon.App.Data;
using GrayMoon.App.Models;
using GrayMoon.App.Models.Api;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GrayMoon.App.Tests;

/// <summary>
/// The branch baseline of the special Workspace (design 5.2, plan package A): recorded from the Worker's CreateBranch
/// answer by New Branch / REST and by Create Branches / Prepare Workspace, never for a branch that already existed or a
/// repository without commits, and deleted with the local branch.
/// </summary>
public sealed class BranchBaselinePersistenceTests
{
    private const string Sha = "4444444444444444444444444444444444444444";

    [Fact]
    public async Task New_Branch_records_the_parent_and_the_commit_and_the_new_HEAD()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        RespondCreated(ctx);

        var outcome = await CreateSingleAsync(ctx, "feature/new", "release/2.4");

        var result = Assert.IsType<CreateBranchApiResult>(outcome.Body);
        Assert.True(result.Success, result.Error);
        Assert.Null(result.Warning);
        var baseline = Assert.Single(await ReadBaselinesAsync(ctx));
        Assert.Equal("feature/new", baseline.BranchName);
        Assert.Equal("release/2.4", baseline.ParentBranchName);
        Assert.Equal(Sha, baseline.BaseCommitSha);
        Assert.Equal("origin/release/2.4", baseline.ResolvedStartRef);
        Assert.Equal(Sha, (await ReadStateAsync(ctx)).HeadCommit);
    }

    [Fact]
    public async Task An_existing_branch_or_a_repository_without_commits_records_nothing()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        ctx.WorkerBridge.Respond("CreateBranch", new { success = true, currentBranch = "feature/new", alreadyExisted = true });
        await CreateSingleAsync(ctx, "feature/new", "main");

        ctx.WorkerBridge.Respond("CreateBranch", new { success = true, currentBranch = "feature/unborn", alreadyExisted = false });
        await CreateSingleAsync(ctx, "feature/unborn", "main");

        Assert.Empty(await ReadBaselinesAsync(ctx));
    }

    [Fact]
    public async Task Creating_the_same_branch_again_replaces_the_row()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        RespondCreated(ctx);
        await CreateSingleAsync(ctx, "feature/new", "release/2.4");
        RespondCreated(ctx, sha: "5555555555555555555555555555555555555555", startRef: "main");
        await CreateSingleAsync(ctx, "feature/new", "main");

        var baseline = Assert.Single(await ReadBaselinesAsync(ctx));
        Assert.Equal("main", baseline.ParentBranchName);
        Assert.Equal("5555555555555555555555555555555555555555", baseline.BaseCommitSha);
    }

    [Fact]
    public async Task Create_Branches_records_a_baseline_for_every_created_branch()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        RespondCreated(ctx, currentBranch: "feature/bulk");
        var special = await ctx.GetSpecialContextIdAsync();

        await using (var scope = ctx.CreateScope())
        {
            var outcome = await scope.ServiceProvider.GetRequiredService<GrayMoon.App.Services.Git.WorkspaceGitService>()
                .CreateBranchesWithOutcomeAsync(ctx.WorkspaceId, special, "feature/bulk", "origin/release/2.4", syncState: true);
            Assert.Empty(outcome.ErrorsByRepositoryId);
            Assert.Empty(outcome.BaselineWarnings);
        }

        var baseline = Assert.Single(await ReadBaselinesAsync(ctx));
        Assert.Equal("feature/bulk", baseline.BranchName);
        // The base as requested, without origin/.
        Assert.Equal("release/2.4", baseline.ParentBranchName);
        Assert.Equal(Sha, (await ReadStateAsync(ctx)).HeadCommit);
    }

    [Fact]
    public async Task Deleting_the_local_branch_deletes_its_baseline()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        RespondCreated(ctx);
        await CreateSingleAsync(ctx, "feature/new", "release/2.4");
        ctx.WorkerBridge.Respond("DeleteBranch", new { success = true });
        var special = await ctx.GetSpecialContextIdAsync();
        // The current branch cannot be deleted: the repository went back to main first.
        await using (var db = await ctx.Resolve<IDbContextFactory<AppDbContext>>().CreateDbContextAsync())
        {
            foreach (var state in db.WorkspaceRepositoryContextStates.Where(s => s.WorkspaceRepositoryId == ctx.WorkspaceRepositoryId))
                state.BranchName = "main";
            foreach (var link in db.WorkspaceRepositories.Where(l => l.WorkspaceRepositoryId == ctx.WorkspaceRepositoryId))
                link.BranchName = "main";
            await db.SaveChangesAsync();
        }

        await using (var scope = ctx.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IWorkspaceBranchOperations>()
                .DeleteBranchAsync(ctx.WorkspaceId, special, ctx.RepositoryId, "feature/new", isRemote: false, force: true);
        }

        Assert.Empty(await ReadBaselinesAsync(ctx));
    }

    [Fact]
    public async Task Unlinking_the_repository_deletes_its_baseline()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        RespondCreated(ctx);
        await CreateSingleAsync(ctx, "feature/new", "release/2.4");

        await using (var db = await ctx.Resolve<IDbContextFactory<AppDbContext>>().CreateDbContextAsync())
        {
            await GrayMoon.App.Repositories.WorkspaceRepositoryLinkCleanup.DeleteDependentsAsync(db, [ctx.WorkspaceRepositoryId], [ctx.RepositoryId], ctx.WorkspaceId);
        }

        Assert.Empty(await ReadBaselinesAsync(ctx));
    }

    private static void RespondCreated(SyncStateTestContext ctx, string sha = Sha, string startRef = "origin/release/2.4", string currentBranch = "feature/new")
        => ctx.WorkerBridge.Respond("CreateBranch", new
        {
            success = true,
            currentBranch,
            branch = currentBranch,
            alreadyExisted = false,
            baseCommitSha = sha,
            resolvedStartRef = startRef,
            outgoingCommits = 0,
            hasUpstream = false,
        });

    private static async Task<BranchHttpOutcome> CreateSingleAsync(SyncStateTestContext ctx, string branch, string baseBranch)
    {
        var special = await ctx.GetSpecialContextIdAsync();
        await using var scope = ctx.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IWorkspaceBranchOperations>()
            .CreateBranchAsync(ctx.WorkspaceId, special, ctx.RepositoryId, branch, baseBranch);
    }

    private static async Task<List<WorkspaceRepositoryBranchBaseline>> ReadBaselinesAsync(SyncStateTestContext ctx)
    {
        await using var db = await ctx.Resolve<IDbContextFactory<AppDbContext>>().CreateDbContextAsync();
        return await db.WorkspaceRepositoryBranchBaselines.AsNoTracking().ToListAsync();
    }

    private static async Task<WorkspaceRepositoryContextState> ReadStateAsync(SyncStateTestContext ctx)
    {
        var special = await ctx.GetSpecialContextIdAsync();
        await using var db = await ctx.Resolve<IDbContextFactory<AppDbContext>>().CreateDbContextAsync();
        return await db.WorkspaceRepositoryContextStates.AsNoTracking()
            .SingleAsync(s => s.WorkspaceFeatureContextId == special.Value && s.WorkspaceRepositoryId == ctx.WorkspaceRepositoryId);
    }
}
