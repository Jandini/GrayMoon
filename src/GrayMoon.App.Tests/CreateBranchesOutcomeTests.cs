using GrayMoon.App.Models.Api;
using GrayMoon.App.Services.Git;
using Microsoft.Extensions.DependencyInjection;

namespace GrayMoon.App.Tests;

/// <summary>
/// Bulk branch creation reports which repositories it attempted and the branch each successful one has checked out,
/// so Prepare Workspace's branch gate can tell a success from a failure or an unreported outcome.
/// </summary>
public sealed class CreateBranchesOutcomeTests
{
    [Fact]
    public async Task Success_reports_the_checked_out_branch_and_persists_state()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        ctx.WorkerBridge.Respond("CreateBranch", new CreateBranchResponse { Success = true, CurrentBranch = "feature/prep", Branch = "feature/prep" });
        var contextId = await ctx.GetSpecialContextIdAsync();

        await using var scope = ctx.CreateScope();
        var git = scope.ServiceProvider.GetRequiredService<WorkspaceGitService>();
        var outcome = await git.CreateBranchesWithOutcomeAsync(ctx.WorkspaceId, contextId, "feature/prep", "__default__", syncState: true);

        Assert.Equal([ctx.RepositoryId], outcome.TargetedRepositoryIds);
        Assert.Equal("feature/prep", outcome.CheckedOutBranchByRepositoryId[ctx.RepositoryId]);
        Assert.Empty(outcome.ErrorsByRepositoryId);
        Assert.Equal("feature/prep", (await ctx.ReadLinkAsync()).BranchName);
    }

    [Fact]
    public async Task Failure_is_reported_as_an_error_and_not_as_a_checkout()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        ctx.WorkerBridge.Respond("CreateBranch", new CreateBranchResponse { Success = false, ErrorMessage = "already exists" });
        var contextId = await ctx.GetSpecialContextIdAsync();

        await using var scope = ctx.CreateScope();
        var git = scope.ServiceProvider.GetRequiredService<WorkspaceGitService>();
        var outcome = await git.CreateBranchesWithOutcomeAsync(ctx.WorkspaceId, contextId, "feature/prep", "__default__", syncState: true);

        Assert.Equal([ctx.RepositoryId], outcome.TargetedRepositoryIds);
        Assert.Empty(outcome.CheckedOutBranchByRepositoryId);
        Assert.Equal("already exists", outcome.ErrorsByRepositoryId[ctx.RepositoryId]);
        Assert.Equal("feature/x", (await ctx.ReadLinkAsync()).BranchName);
    }

    /// <summary>
    /// The Worker checks out a branch that is already there instead of creating it. That used to look like a created
    /// branch; the outcome now names the repository so the caller can say the base was not used.
    /// </summary>
    [Fact]
    public async Task A_branch_that_already_existed_is_a_success_that_names_the_repository()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        ctx.WorkerBridge.Respond("CreateBranch", new CreateBranchResponse { Success = true, CurrentBranch = "feature/prep", Branch = "feature/prep", AlreadyExisted = true });
        var contextId = await ctx.GetSpecialContextIdAsync();

        await using var scope = ctx.CreateScope();
        var git = scope.ServiceProvider.GetRequiredService<WorkspaceGitService>();
        var outcome = await git.CreateBranchesWithOutcomeAsync(ctx.WorkspaceId, contextId, "feature/prep", "__default__", syncState: true);

        Assert.Empty(outcome.ErrorsByRepositoryId);
        Assert.Equal("feature/prep", outcome.CheckedOutBranchByRepositoryId[ctx.RepositoryId]);
        Assert.Equal("graymoon-api", Assert.Single(outcome.AlreadyExistedByRepositoryId).Value);
        Assert.Equal(
            "Branch 'feature/prep' already existed in graymoon-api. It was checked out as it is and was not created from the base branch.",
            GrayMoon.Application.BranchAlreadyExistedWarning.Describe("feature/prep", [.. outcome.AlreadyExistedByRepositoryId.Values]));
    }

    [Fact]
    public async Task A_created_branch_is_not_reported_as_already_existing()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        ctx.WorkerBridge.Respond("CreateBranch", new CreateBranchResponse { Success = true, CurrentBranch = "feature/prep", Branch = "feature/prep" });
        var contextId = await ctx.GetSpecialContextIdAsync();

        await using var scope = ctx.CreateScope();
        var git = scope.ServiceProvider.GetRequiredService<WorkspaceGitService>();
        var outcome = await git.CreateBranchesWithOutcomeAsync(ctx.WorkspaceId, contextId, "feature/prep", "__default__", syncState: true);

        Assert.Empty(outcome.AlreadyExistedByRepositoryId);
    }

    [Fact]
    public void The_warning_names_up_to_five_repositories_and_counts_the_rest()
    {
        Assert.Null(GrayMoon.Application.BranchAlreadyExistedWarning.Describe("feature/x", []));
        Assert.Null(GrayMoon.Application.BranchAlreadyExistedWarning.Describe("feature/x", null));
        Assert.Equal(
            "Branch 'feature/x' already existed in 2 repositories (api, web). It was checked out as it is and was not created from the base branch.",
            GrayMoon.Application.BranchAlreadyExistedWarning.Describe("feature/x", ["web", "api"]));
        Assert.Equal(
            "Branch 'feature/x' already existed in 7 repositories (r1, r2, r3, r4, r5, and 2 more). It was checked out as it is and was not created from the base branch.",
            GrayMoon.Application.BranchAlreadyExistedWarning.Describe("feature/x", ["r1", "r2", "r3", "r4", "r5", "r6", "r7"]));
    }

    [Fact]
    public async Task Legacy_overload_still_returns_only_the_errors()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        ctx.WorkerBridge.Respond("CreateBranch", new CreateBranchResponse { Success = false, ErrorMessage = "boom" });
        var contextId = await ctx.GetSpecialContextIdAsync();

        await using var scope = ctx.CreateScope();
        var git = scope.ServiceProvider.GetRequiredService<WorkspaceGitService>();
        var errors = await git.CreateBranchesAsync(ctx.WorkspaceId, contextId, "feature/prep", "__default__");

        Assert.Equal("boom", errors[ctx.RepositoryId]);
    }
}
