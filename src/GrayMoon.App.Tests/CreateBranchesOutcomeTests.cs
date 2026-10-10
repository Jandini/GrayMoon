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
