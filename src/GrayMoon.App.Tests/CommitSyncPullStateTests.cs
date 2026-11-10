using GrayMoon.App.Data;
using GrayMoon.App.Models;
using GrayMoon.App.Services;
using GrayMoon.Application.Features;
using Microsoft.EntityFrameworkCore;
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

    /// <summary>Response shape of a worker that predates the state snapshot: two counts and nothing else.</summary>
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
        var special = await ctx.GetSpecialContextIdAsync();
        await handler.CommitSyncAsync(
            ctx.WorkspaceId,
            special,
            ctx.RepositoryId,
            CancellationToken.None,
            new Progress<OperationProgress>(_ => { }),
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
        ctx.WorkerBridge.Respond("CommitSyncRepository", PulledResponse());

        await RunPullAsync(ctx);

        var link = await ctx.ReadLinkAsync();
        Assert.Equal(0, link.IncomingCommits);
        Assert.Equal(0, link.DefaultBranchBehindCommits);
        Assert.Equal(0, link.DefaultBranchAheadCommits);
        Assert.Equal("2.0.0", link.GitVersion);
        Assert.Equal(RepoSyncStatus.InSync, link.SyncStatus);
    }

    [Fact]
    public async Task Pull_from_a_worker_without_a_state_snapshot_leaves_the_counts_it_cannot_report()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        await ctx.MutateLinkAsync(link =>
        {
            link.BranchName = "main";
            link.DefaultBranchBehindCommits = 2;
        });
        ctx.WorkerBridge.Respond("CommitSyncRepository", LegacyPulledResponse());

        await RunPullAsync(ctx);

        var link = await ctx.ReadLinkAsync();
        Assert.Equal("main", link.BranchName);
        Assert.Equal(2, link.DefaultBranchBehindCommits);
    }

    [Fact]
    public async Task Pull_failure_sets_repo_error_not_page_error()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        const string gitError = "Please commit your changes or stash them before you merge. Aborting";
        ctx.WorkerBridge.Respond("CommitSyncRepository", data: null, success: false, error: gitError);

        string? repoError = null;
        string? pageError = null;
        await using var scope = ctx.CreateScope();
        var handler = scope.ServiceProvider.GetRequiredService<WorkspaceCommitSyncHandler>();
        var special = await ctx.GetSpecialContextIdAsync();
        await handler.CommitSyncAsync(
            ctx.WorkspaceId,
            special,
            ctx.RepositoryId,
            CancellationToken.None,
            new Progress<OperationProgress>(_ => { }),
            (_, msg) => repoError = msg,
            err => pageError = err);

        Assert.Equal(gitError, repoError);
        Assert.Null(pageError);
    }

    [Fact]
    public async Task Pull_failure_in_the_Workspace_marks_the_link_and_its_context_state_as_failed()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        ctx.WorkerBridge.Respond("CommitSyncRepository", data: null, success: false, error: "pull failed");

        await RunPullAsync(ctx);

        Assert.Equal(RepoSyncStatus.Error, (await ctx.ReadLinkAsync()).SyncStatus);
        var special = await ctx.GetSpecialContextIdAsync();
        Assert.Equal(RepoSyncStatus.Error, (await ReadStateAsync(ctx, special)).SyncStatus);
    }

    [Fact]
    public async Task Pull_failure_in_a_Feature_marks_the_Feature_state_and_leaves_the_shared_link_alone()
    {
        // The failure used to be written to the shared link: the Workspace row turned red for a Feature's failed pull,
        // and the Feature's own row stayed in sync.
        await using var ctx = await SyncStateTestContext.CreateAsync();
        var feature = await SeedFeatureAsync(ctx);
        var linkStatusBefore = (await ctx.ReadLinkAsync()).SyncStatus;
        Assert.NotEqual(RepoSyncStatus.Error, linkStatusBefore);
        ctx.WorkerBridge.Respond("CommitSyncRepository", data: null, success: false, error: "pull failed");

        string? repoError = null;
        await using (var scope = ctx.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<WorkspaceCommitSyncHandler>().CommitSyncAsync(
                ctx.WorkspaceId,
                feature,
                ctx.RepositoryId,
                CancellationToken.None,
                new Progress<OperationProgress>(_ => { }),
                (_, message) => repoError = message,
                _ => { });
        }

        Assert.Equal("pull failed", repoError);
        Assert.Equal(linkStatusBefore, (await ctx.ReadLinkAsync()).SyncStatus);
        Assert.Equal(RepoSyncStatus.Error, (await ReadStateAsync(ctx, feature)).SyncStatus);
    }

    private static async Task<WorkspaceRepositoryContextState> ReadStateAsync(SyncStateTestContext ctx, WorkspaceFeatureContextId contextId)
    {
        await using var db = await ctx.Resolve<IDbContextFactory<AppDbContext>>().CreateDbContextAsync();
        return await db.WorkspaceRepositoryContextStates.AsNoTracking().SingleAsync(s =>
            s.WorkspaceFeatureContextId == contextId.Value && s.WorkspaceRepositoryId == ctx.WorkspaceRepositoryId);
    }

    private static async Task<WorkspaceFeatureContextId> SeedFeatureAsync(SyncStateTestContext ctx)
    {
        await using var db = await ctx.Resolve<IDbContextFactory<AppDbContext>>().CreateDbContextAsync();
        var feature = new WorkspaceFeature
        {
            WorkspaceId = ctx.WorkspaceId,
            Name = "feature-y",
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
            WorktreePath = @"C:\Users\test\.graymoon\test-ws\features\feature-y\graymoon-api",
            State = WorkspaceFeatureRepositoryState.Ready,
            BaseCommitSha = "abc123",
            ParentBranchName = "main",
            CreatedAt = DateTime.UtcNow,
        });
        db.WorkspaceRepositoryContextStates.Add(new WorkspaceRepositoryContextState
        {
            WorkspaceFeatureContextId = context.WorkspaceFeatureContextId,
            WorkspaceRepositoryId = ctx.WorkspaceRepositoryId,
            BranchName = "feature/y",
            SyncStatus = RepoSyncStatus.InSync,
        });
        await db.SaveChangesAsync();
        return new WorkspaceFeatureContextId(context.WorkspaceFeatureContextId);
    }
}
