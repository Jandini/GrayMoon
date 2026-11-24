using GrayMoon.Abstractions.Notifications;
using GrayMoon.App.Models;
using GrayMoon.App.Services;
using GrayMoon.App.Services.Workspaces;
using GrayMoon.Application.Features;
using Microsoft.Extensions.DependencyInjection;

namespace GrayMoon.App.Tests;

/// <summary>
/// Hook snapshots wait in a serial queue. Remove Feature once queued about two per repository, the queue took
/// four minutes to drain, and a pull that finished in the meantime had its result overwritten by snapshots taken
/// before it: the grid showed incoming commits again until the next sync.
/// </summary>
public sealed class StaleHookSnapshotTests
{
    private static object PulledResponse() => new
    {
        success = true,
        mergeConflict = false,
        version = "0.1.0-main.155",
        branch = "main",
        state = new
        {
            branchName = "main",
            gitVersion = "0.1.0-main.155",
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

    /// <summary>What the push hook reported for the main checkout before the pull: eight commits behind.</summary>
    private static RepositorySyncNotification BehindHookNotification(SyncStateTestContext ctx) => new()
    {
        WorkspaceId = ctx.WorkspaceId,
        RepositoryId = ctx.RepositoryId,
        Version = "0.1.0-main.147",
        Branch = "main",
        State = new RepositoryStateSnapshot
        {
            BranchName = "main",
            GitVersion = "0.1.0-main.147",
            DefaultBranchName = "main",
            OutgoingCommits = 0,
            IncomingCommits = 8,
            DefaultBranchBehind = 8,
            DefaultBranchAhead = 0,
            HasUpstream = true,
            IdentityProbed = true,
            GitVersionProbed = true,
            CommitCountsProbed = true,
            UpstreamProbed = true,
        },
    };

    private static async Task SeedBehindAsync(SyncStateTestContext ctx) =>
        await ctx.MutateLinkAsync(link =>
        {
            link.BranchName = "main";
            link.GitVersion = "0.1.0-main.147";
            link.IncomingCommits = 8;
            link.DefaultBranchBehindCommits = 8;
        });

    private static async Task RunPullAsync(SyncStateTestContext ctx)
    {
        await using var scope = ctx.CreateScope();
        var special = await ctx.GetSpecialContextIdAsync();
        await scope.ServiceProvider.GetRequiredService<WorkspaceCommitSyncHandler>().CommitSyncAsync(
            ctx.WorkspaceId,
            special,
            ctx.RepositoryId,
            CancellationToken.None,
            new Progress<OperationProgress>(_ => { }),
            (_, _) => { },
            _ => { });
    }

    private static async Task HandleHookAsync(SyncStateTestContext ctx, RepositorySyncNotification notification, long receivedAt)
    {
        await using var scope = ctx.CreateScope();
        await scope.ServiceProvider.GetRequiredService<SyncCommandHandler>().HandleAsync(notification, receivedAt);
    }

    [Fact]
    public async Task A_hook_snapshot_queued_before_a_pull_does_not_overwrite_the_pull_result()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        await SeedBehindAsync(ctx);
        ctx.WorkerBridge.Respond("CommitSyncRepository", PulledResponse());

        // The hook snapshot reaches the App and waits in the queue while the pull runs.
        var receivedAt = RepositoryStateFreshness.Now();
        var queued = BehindHookNotification(ctx);
        await RunPullAsync(ctx);
        Assert.Equal(0, (await ctx.ReadLinkAsync()).IncomingCommits);
        var broadcastsAfterPull = ctx.Broadcasts.Count;

        await HandleHookAsync(ctx, queued, receivedAt);

        var link = await ctx.ReadLinkAsync();
        Assert.Equal(0, link.IncomingCommits);
        Assert.Equal(0, link.DefaultBranchBehindCommits);
        Assert.Equal("0.1.0-main.155", link.GitVersion);
        // Nothing changed, so the grid is not told to reload and no recompute runs for it.
        Assert.Equal(broadcastsAfterPull, ctx.Broadcasts.Count);
    }

    /// <summary>
    /// Log 2026-11-24 21:07, Edx1.File.Backup: Merge with "return to default" returned the repository to main at
    /// 21:07:07, and a push-hook snapshot taken on file-format before that was applied from the queue at 21:07:10. The
    /// grid showed file-format again, although the checkout was on main.
    /// </summary>
    [Fact]
    public async Task A_hook_snapshot_queued_before_a_return_to_default_does_not_put_the_left_branch_back()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        await ctx.MutateLinkAsync(link =>
        {
            link.BranchName = "file-format";
            link.GitVersion = "0.1.0-file-format.9";
        });
        ctx.WorkerBridge.Respond("ReturnToDefaultBranch", new GrayMoon.App.Models.Api.ReturnToDefaultBranchResponse
        {
            Success = true,
            CurrentBranch = "main",
            DefaultBranch = "main",
            LocalBranches = ["main"],
            RemoteBranches = ["origin/main"],
            Tags = [],
            GitVersion = "0.1.0-main.160",
        }.WithProbedState());

        var receivedAt = RepositoryStateFreshness.Now();
        var queued = new RepositorySyncNotification
        {
            WorkspaceId = ctx.WorkspaceId,
            RepositoryId = ctx.RepositoryId,
            Version = "0.1.0-file-format.9",
            Branch = "file-format",
            State = WorkerStateTestExtensions.HookState("file-format", "0.1.0-file-format.9", 0, 0, true, headCommit: "ffffffffffffffffffffffffffffffffffffffff"),
        };

        await using (var scope = ctx.CreateScope())
        {
            var special = await ctx.GetSpecialContextIdAsync();
            var outcome = await scope.ServiceProvider.GetRequiredService<WorkspaceGitService>().ReturnToDefaultDirectAsync(
                ctx.WorkspaceId, special, ctx.RepositoryId, "file-format",
                deleteRemoteBranch: false, allowForceDeleteLocalBranch: true, CancellationToken.None);
            Assert.True(outcome.Success, outcome.ErrorMessage);
        }

        await HandleHookAsync(ctx, queued, receivedAt);

        var link = await ctx.ReadLinkAsync();
        Assert.Equal("main", link.BranchName);
        Assert.Equal("0.1.0-main.160", link.GitVersion);
    }

    [Fact]
    public async Task A_hook_snapshot_received_after_a_pull_is_applied()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        await SeedBehindAsync(ctx);
        ctx.WorkerBridge.Respond("CommitSyncRepository", PulledResponse());
        await RunPullAsync(ctx);

        // New commits reached the remote after the pull; a later hook reports them.
        await HandleHookAsync(ctx, BehindHookNotification(ctx), RepositoryStateFreshness.Now());

        var link = await ctx.ReadLinkAsync();
        Assert.Equal(8, link.IncomingCommits);
        Assert.Equal("0.1.0-main.147", link.GitVersion);
    }

    [Fact]
    public async Task A_failed_pull_does_not_supersede_a_queued_hook_snapshot()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        await ctx.MutateLinkAsync(link =>
        {
            link.BranchName = "main";
            link.IncomingCommits = 0;
        });
        ctx.WorkerBridge.Respond("CommitSyncRepository", data: null, success: false, error: "pull failed");

        var receivedAt = RepositoryStateFreshness.Now();
        await RunPullAsync(ctx);
        await HandleHookAsync(ctx, BehindHookNotification(ctx), receivedAt);

        // The failed pull read nothing, so the queued snapshot is still the newest state there is.
        Assert.Equal(8, (await ctx.ReadLinkAsync()).IncomingCommits);
    }

    [Fact]
    public void Groups_the_operation_did_not_write_are_kept()
    {
        var freshness = new RepositoryStateFreshness();
        var context = new WorkspaceFeatureContextId(7);
        freshness.RecordOperationWrite(
            context,
            repositoryId: 3,
            new RepositoryStateSnapshot { OutgoingCommits = 0, IncomingCommits = 0, CommitCountsProbed = true },
            operationStartedAt: 200);

        var hook = new RepositoryStateSnapshot
        {
            BranchName = "main",
            IncomingCommits = 8,
            LocalBranches = ["main"],
            RemoteBranches = ["origin/main"],
            Tags = [],
            IdentityProbed = true,
            CommitCountsProbed = true,
            BranchesProbed = true,
        };
        var kept = freshness.WithoutSupersededGroups(context, 3, hook, receivedAt: 100);

        Assert.NotNull(kept);
        Assert.True(kept.IdentityProbed);
        Assert.Equal("main", kept.BranchName);
        Assert.True(kept.BranchesProbed);
        Assert.False(kept.CommitCountsProbed);
        Assert.Null(kept.IncomingCommits);
    }

    [Fact]
    public void A_superseded_identity_takes_the_groups_that_describe_the_checkout_with_it()
    {
        var freshness = new RepositoryStateFreshness();
        var context = new WorkspaceFeatureContextId(7);
        freshness.RecordOperationWrite(
            context,
            repositoryId: 3,
            new RepositoryStateSnapshot { BranchName = "release", IdentityProbed = true },
            operationStartedAt: 200);

        // Counts and version were measured for the branch the hook saw, not for the one the operation left checked out.
        var hook = new RepositoryStateSnapshot
        {
            BranchName = "main",
            GitVersion = "1.0.0",
            IncomingCommits = 8,
            HasUpstream = true,
            IdentityProbed = true,
            GitVersionProbed = true,
            CommitCountsProbed = true,
            UpstreamProbed = true,
        };

        Assert.Null(freshness.WithoutSupersededGroups(context, 3, hook, receivedAt: 100));
    }

    [Fact]
    public void Another_repository_or_context_is_not_affected()
    {
        var freshness = new RepositoryStateFreshness();
        var context = new WorkspaceFeatureContextId(7);
        freshness.RecordOperationWrite(
            context,
            repositoryId: 3,
            new RepositoryStateSnapshot { BranchName = "main", IdentityProbed = true },
            operationStartedAt: 200);
        var hook = new RepositoryStateSnapshot { BranchName = "main", IdentityProbed = true };

        Assert.Same(hook, freshness.WithoutSupersededGroups(context, 4, hook, receivedAt: 100));
        Assert.Same(hook, freshness.WithoutSupersededGroups(new WorkspaceFeatureContextId(8), 3, hook, receivedAt: 100));
        Assert.Same(hook, freshness.WithoutSupersededGroups(context, 3, hook, receivedAt: 200));
    }
}
