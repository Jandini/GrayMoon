using GrayMoon.App.Models;
using GrayMoon.App.Services;
using Microsoft.Extensions.DependencyInjection;

namespace GrayMoon.App.Tests;

/// <summary>
/// A pull moves the branch, so it changes the comparison against the default branch as well as the one
/// against the upstream. The commit-sync write path used to persist only outgoing and incoming, which left
/// the ahead/behind badge showing pre-pull numbers until an unrelated flow happened to refresh it.
/// </summary>
public sealed class CommitSyncPullStateTests
{
    private static object PulledResponse() => new
    {
        success = true,
        mergeConflict = false,
        version = "2.0.0",
        branch = "main",
        outgoingCommits = 0,
        incomingCommits = 0,
        defaultBranchBehind = 0,
        defaultBranchAhead = 0,
        hasUpstream = true,
        state = new
        {
            branchName = "main",
            gitVersion = "2.0.0",
            defaultBranchName = "main",
            outgoingCommits = 0,
            incomingCommits = 0,
            defaultBranchBehind = 0,
            defaultBranchAhead = 0,
            hasUpstream = true,
            identityProbed = true,
            gitVersionProbed = true,
            commitCountsProbed = true,
            upstreamProbed = true,
        },
    };

    /// <summary>Response shape of an agent that predates the state snapshot: two counts and nothing else.</summary>
    private static object LegacyPulledResponse() => new
    {
        success = true,
        mergeConflict = false,
        version = "2.0.0",
        branch = "main",
        outgoingCommits = 0,
        incomingCommits = 0,
    };

    private static async Task RunPullAsync(SyncStateTestContext ctx)
    {
        await using var scope = ctx.CreateScope();
        var handler = scope.ServiceProvider.GetRequiredService<WorkspaceCommitSyncHandler>();
        await handler.CommitSyncAsync(
            ctx.WorkspaceId,
            ctx.RepositoryId,
            CancellationToken.None,
            _ => Task.CompletedTask,
            (_, _) => { },
            _ => { });
    }

    [Fact]
    public async Task Pull_refreshes_the_comparison_against_the_default_branch()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        await ctx.MutateLinkAsync(link =>
        {
            link.BranchName = "main";
            link.IncomingCommits = 2;
            link.DefaultBranchBehindCommits = 2;
            link.DefaultBranchAheadCommits = 0;
        });
        ctx.AgentBridge.Respond("CommitSyncRepository", PulledResponse());

        await RunPullAsync(ctx);

        var link = await ctx.ReadLinkAsync();
        Assert.Equal(0, link.IncomingCommits);
        Assert.Equal(0, link.DefaultBranchBehindCommits);
        Assert.Equal(0, link.DefaultBranchAheadCommits);
        Assert.Equal("2.0.0", link.GitVersion);
        Assert.Equal(RepoSyncStatus.InSync, link.SyncStatus);
    }

    [Fact]
    public async Task Pull_from_an_agent_without_a_state_snapshot_leaves_the_counts_it_cannot_report()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        await ctx.MutateLinkAsync(link =>
        {
            link.BranchName = "main";
            link.DefaultBranchBehindCommits = 2;
        });
        ctx.AgentBridge.Respond("CommitSyncRepository", LegacyPulledResponse());

        await RunPullAsync(ctx);

        var link = await ctx.ReadLinkAsync();
        Assert.Equal("main", link.BranchName);
        Assert.Equal(2, link.DefaultBranchBehindCommits);
    }
}
