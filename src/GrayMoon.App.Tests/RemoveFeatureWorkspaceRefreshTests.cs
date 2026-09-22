using GrayMoon.Abstractions.Agent;
using GrayMoon.App.Data;
using GrayMoon.App.Models;
using GrayMoon.App.Models.Api;
using GrayMoon.Application.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GrayMoon.App.Tests;

/// <summary>
/// After Remove Feature, Workspace is often already on default - ordinary Return to Default skips those
/// repos. These tests assert Remove Feature still refreshes Incoming/Outgoing/HasUpstream via either
/// ReturnToDefaultDirect (pull on) or Sync (pull off).
/// </summary>
public sealed class RemoveFeatureWorkspaceRefreshTests
{
    private static ReturnToDefaultBranchResponse ReturnToDefaultOk(int incoming = 4) => new()
    {
        Success = true,
        CurrentBranch = "main",
        DefaultBranch = "main",
        LocalBranches = ["main"],
        RemoteBranches = ["origin/main"],
        Tags = [],
        OutgoingCommits = 0,
        IncomingCommits = incoming,
        HasUpstream = true,
        DefaultBranchBehind = 0,
        DefaultBranchAhead = 0,
        GitVersion = "2.0.0",
        Projects =
        [
            new AgentProjectDto
            {
                Name = "Acme.Api",
                ProjectType = (int)ProjectType.Service,
                ProjectPath = "src/Acme.Api/Acme.Api.csproj",
                TargetFramework = "net10.0",
            }
        ],
    };

    private static object SyncResponse(int incoming = 4) => new
    {
        success = true,
        version = "2.0.0",
        branch = "main",
        defaultBranch = "main",
        outgoingCommits = 0,
        incomingCommits = incoming,
        defaultBranchBehind = 0,
        defaultBranchAhead = 0,
        hasUpstream = true,
        upstreamProbed = true,
        localBranches = new[] { "main" },
        remoteBranches = new[] { "origin/main" },
        tags = Array.Empty<string>(),
        projects = Array.Empty<object>(),
    };

    [Fact]
    public async Task Remove_with_pull_calls_ReturnToDefault_even_when_Workspace_already_on_default()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        var featureContextId = await SeedRemovableFeatureAsync(ctx);

        // Already on default with a stale snapshot (the bug: badges lie until Sync).
        await ctx.MutateLinkAsync(link =>
        {
            link.BranchName = "main";
            link.DefaultBranchName = "main";
            link.OutgoingCommits = 0;
            link.IncomingCommits = 0;
            link.BranchHasUpstream = false;
        });

        ctx.AgentBridge.Respond(AgentHubMethods.RemoveGitWorktree, new { success = true });
        ctx.AgentBridge.Respond("DeleteBranch", new { success = true });
        ctx.AgentBridge.Respond("ReturnToDefaultBranch", ReturnToDefaultOk(incoming: 7));

        await using var scope = ctx.CreateScope();
        var ops = scope.ServiceProvider.GetRequiredService<IWorkspaceFeatureOperations>();
        var result = await ops.RemoveFeatureAsync(
            featureContextId,
            new RemoveFeatureOptions
            {
                AllowDiscardUncommitted = true,
                AllowForceDeleteLocalBranches = true,
                ReturnWorkspaceToDefaultAndPull = true,
            });

        Assert.True(result.Success, result.Error);

        Assert.Contains(ctx.AgentBridge.Calls, c => c.Command == "ReturnToDefaultBranch");
        Assert.DoesNotContain(ctx.AgentBridge.Calls, c => c.Command == "SyncRepository");

        var link = await ctx.ReadLinkAsync();
        Assert.Equal("main", link.BranchName);
        Assert.Equal(7, link.IncomingCommits);
        Assert.Equal(0, link.OutgoingCommits);
        Assert.True(link.BranchHasUpstream);

        await AssertFeatureGoneAsync(ctx, featureContextId);
    }

    [Fact]
    public async Task Remove_without_pull_Syncs_Workspace_snapshot_when_already_on_default()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        var featureContextId = await SeedRemovableFeatureAsync(ctx);

        await ctx.MutateLinkAsync(link =>
        {
            link.BranchName = "main";
            link.DefaultBranchName = "main";
            link.OutgoingCommits = 0;
            link.IncomingCommits = 0;
            link.BranchHasUpstream = false;
        });

        ctx.AgentBridge.Respond(AgentHubMethods.RemoveGitWorktree, new { success = true });
        ctx.AgentBridge.Respond("DeleteBranch", new { success = true });
        ctx.AgentBridge.Respond("SyncRepository", SyncResponse(incoming: 5));
        ctx.AgentBridge.Respond("CheckFileVersions", new { success = true, files = Array.Empty<object>() });

        await using var scope = ctx.CreateScope();
        var ops = scope.ServiceProvider.GetRequiredService<IWorkspaceFeatureOperations>();
        var result = await ops.RemoveFeatureAsync(
            featureContextId,
            new RemoveFeatureOptions
            {
                AllowDiscardUncommitted = true,
                AllowForceDeleteLocalBranches = true,
                ReturnWorkspaceToDefaultAndPull = false,
            });

        Assert.True(result.Success, result.Error);

        Assert.Contains(ctx.AgentBridge.Calls, c => c.Command == "SyncRepository");
        Assert.DoesNotContain(ctx.AgentBridge.Calls, c => c.Command == "ReturnToDefaultBranch");

        var link = await ctx.ReadLinkAsync();
        Assert.Equal("main", link.BranchName);
        Assert.Equal(5, link.IncomingCommits);
        Assert.True(link.BranchHasUpstream);

        await AssertFeatureGoneAsync(ctx, featureContextId);
    }

    [Fact]
    public async Task Remove_when_worktree_delete_fails_does_not_report_success_or_refresh_workspace()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        var featureContextId = await SeedRemovableFeatureAsync(ctx);

        ctx.AgentBridge.Respond(
            AgentHubMethods.RemoveGitWorktree,
            data: null,
            success: false,
            error: "error: failed to delete 'features/feat-refresh': Permission denied");

        await using var scope = ctx.CreateScope();
        var ops = scope.ServiceProvider.GetRequiredService<IWorkspaceFeatureOperations>();
        var result = await ops.RemoveFeatureAsync(
            featureContextId,
            new RemoveFeatureOptions
            {
                AllowDiscardUncommitted = true,
                AllowForceDeleteLocalBranches = true,
                ReturnWorkspaceToDefaultAndPull = true,
            });

        Assert.False(result.Success);
        Assert.Contains("Permission denied", result.Error);
        Assert.DoesNotContain(ctx.AgentBridge.Calls, c => c.Command == "ReturnToDefaultBranch");
        Assert.DoesNotContain(ctx.AgentBridge.Calls, c => c.Command == "SyncRepository");
        Assert.DoesNotContain(ctx.AgentBridge.Calls, c => c.Command == "DeleteBranch");

        await using var read = ctx.CreateScope();
        var db = read.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.True(await db.WorkspaceFeatureContexts.AnyAsync(c => c.WorkspaceFeatureContextId == featureContextId.Value));
        var feature = await db.WorkspaceFeatures.SingleAsync(f => f.Name == "feat-refresh" && f.WorkspaceId == ctx.WorkspaceId);
        Assert.Equal(WorkspaceFeatureLifecycleState.NeedsRepair, feature.LifecycleState);
    }

    private static async Task<WorkspaceFeatureContextId> SeedRemovableFeatureAsync(SyncStateTestContext ctx)
    {
        await using var scope = ctx.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var feature = new WorkspaceFeature
        {
            WorkspaceId = ctx.WorkspaceId,
            Name = "feat-refresh",
            LifecycleState = WorkspaceFeatureLifecycleState.Ready,
            BaseKind = WorkspaceFeatureBaseKind.CurrentWorkspace,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        db.WorkspaceFeatures.Add(feature);
        await db.SaveChangesAsync();

        var context = new WorkspaceFeatureContext
        {
            WorkspaceId = ctx.WorkspaceId,
            Kind = WorkspaceFeatureContextKind.Feature,
            WorkspaceFeatureId = feature.WorkspaceFeatureId,
            CreatedAt = DateTime.UtcNow,
            IsInSync = true,
        };
        db.WorkspaceFeatureContexts.Add(context);
        await db.SaveChangesAsync();

        db.WorkspaceFeatureRepositories.Add(new WorkspaceFeatureRepository
        {
            WorkspaceFeatureContextId = context.WorkspaceFeatureContextId,
            WorkspaceRepositoryId = ctx.WorkspaceRepositoryId,
            WorktreePath = @"C:\gm-test-root\.graymoon\test-ws\features\feat-refresh\graymoon-api",
            State = WorkspaceFeatureRepositoryState.Ready,
            BaseCommitSha = "abc123",
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        return new WorkspaceFeatureContextId(context.WorkspaceFeatureContextId);
    }

    private static async Task AssertFeatureGoneAsync(SyncStateTestContext ctx, WorkspaceFeatureContextId featureContextId)
    {
        await using var scope = ctx.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await db.WorkspaceFeatureContexts.AnyAsync(c => c.WorkspaceFeatureContextId == featureContextId.Value));
        Assert.False(await db.WorkspaceFeatures.AnyAsync(f => f.Name == "feat-refresh" && f.WorkspaceId == ctx.WorkspaceId));
    }
}
