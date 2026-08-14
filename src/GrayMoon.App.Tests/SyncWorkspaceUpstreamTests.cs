using GrayMoon.App.Services;
using Microsoft.Extensions.DependencyInjection;

namespace GrayMoon.App.Tests;

/// <summary>
/// Sync is the flow users reach for when a row looks wrong, so it has to be able to repair every badge it
/// reports - including the upstream flag, which a stale <c>false</c> used to survive indefinitely because no
/// sync-side writer touched it.
/// </summary>
public sealed class SyncWorkspaceUpstreamTests
{
    private static object SyncResponse(bool? hasUpstream, bool upstreamProbed) => new
    {
        success = true,
        version = "2.0.0",
        branch = "main",
        defaultBranch = "main",
        outgoingCommits = 0,
        incomingCommits = 0,
        defaultBranchBehind = 0,
        defaultBranchAhead = 0,
        hasUpstream,
        upstreamProbed,
        localBranches = new[] { "main" },
        remoteBranches = new[] { "origin/main" },
        tags = Array.Empty<string>(),
        projects = Array.Empty<object>(),
    };

    [Fact]
    public async Task Sync_repairs_a_stale_no_upstream_flag()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        await ctx.MutateLinkAsync(link => link.BranchHasUpstream = false);
        ctx.AgentBridge.Respond("SyncRepository", SyncResponse(hasUpstream: true, upstreamProbed: true));

        await using var scope = ctx.CreateScope();
        var git = scope.ServiceProvider.GetRequiredService<WorkspaceGitService>();
        await git.SyncAsync(ctx.WorkspaceId);

        var link = await ctx.ReadLinkAsync();
        Assert.Equal("main", link.BranchName);
        Assert.True(link.BranchHasUpstream);
    }

    [Fact]
    public async Task Sync_from_an_agent_that_does_not_probe_upstream_leaves_the_flag_alone()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        await ctx.MutateLinkAsync(link => link.BranchHasUpstream = false);
        ctx.AgentBridge.Respond("SyncRepository", SyncResponse(hasUpstream: null, upstreamProbed: false));

        await using var scope = ctx.CreateScope();
        var git = scope.ServiceProvider.GetRequiredService<WorkspaceGitService>();
        await git.SyncAsync(ctx.WorkspaceId);

        var link = await ctx.ReadLinkAsync();
        Assert.False(link.BranchHasUpstream);
    }
}
