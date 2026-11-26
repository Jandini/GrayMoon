using GrayMoon.Abstractions.Notifications;
using GrayMoon.App.Data;
using GrayMoon.App.Models;
using GrayMoon.App.Services.Workspaces;
using GrayMoon.Application.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace GrayMoon.App.Tests;

/// <summary>
/// The order of two reports about one repository is decided from evidence, not from the order they arrive in: when
/// an operation began, and the Worker's capture interval of a hook snapshot. These tests hand the reports over in
/// a chosen order, with chosen capture stamps; nothing waits or sleeps.
/// </summary>
public sealed class RepositoryStateEvidenceTests
{
    private const string Session = "worker-session-1";
    private static readonly WorkspaceFeatureContextId ContextA = new(11);
    private static readonly WorkspaceFeatureContextId ContextB = new(12);
    private const int WorkspaceId = 7;
    private const int Repo = 3;

    // ---------------------------------------------------------------- the verdict (no database)

    [Fact]
    public void A_snapshot_captured_before_the_one_already_applied_is_dropped_although_it_arrived_later()
    {
        var freshness = new RepositoryStateFreshness();
        freshness.RecordApplied(ContextA, Repo, Checkout("bbb", start: 20, end: 21), WorkspaceId);

        var verdict = freshness.Evaluate(ContextA, Repo, Checkout("aaa", start: 10, end: 11), RepositoryStateFreshness.Now(), WorkspaceId);

        Assert.Null(verdict.Snapshot);
        Assert.False(verdict.Duplicate);
        Assert.Equal(RepositoryFactGroups.Identity | RepositoryFactGroups.CommitCounts, verdict.Superseded);
        Assert.Equal(RepositoryFactGroups.None, verdict.Ambiguous);
    }

    [Fact]
    public void A_snapshot_captured_after_the_one_already_applied_is_applied_as_it_is()
    {
        var freshness = new RepositoryStateFreshness();
        freshness.RecordApplied(ContextA, Repo, Checkout("aaa", start: 10, end: 11), WorkspaceId);
        var newer = Checkout("bbb", start: 20, end: 21);

        var verdict = freshness.Evaluate(ContextA, Repo, newer, RepositoryStateFreshness.Now(), WorkspaceId);

        Assert.Same(newer, verdict.Snapshot);
        Assert.Equal(RepositoryFactGroups.None, verdict.Superseded | verdict.Ambiguous);
    }

    [Fact]
    public void The_same_capture_delivered_twice_is_a_duplicate()
    {
        var freshness = new RepositoryStateFreshness();
        var snapshot = Checkout("aaa", start: 10, end: 11);
        freshness.RecordApplied(ContextA, Repo, snapshot, WorkspaceId);

        var verdict = freshness.Evaluate(ContextA, Repo, Checkout("aaa", start: 10, end: 11), RepositoryStateFreshness.Now(), WorkspaceId);

        Assert.True(verdict.Duplicate);
        Assert.Null(verdict.Snapshot);
    }

    [Fact]
    public void Two_captures_that_overlap_cannot_be_ordered_so_the_snapshot_is_applied_and_reported_as_ambiguous()
    {
        var freshness = new RepositoryStateFreshness();
        freshness.RecordApplied(ContextA, Repo, Checkout("aaa", start: 10, end: 14), WorkspaceId);
        var overlapping = Checkout("bbb", start: 12, end: 16);

        var verdict = freshness.Evaluate(ContextA, Repo, overlapping, RepositoryStateFreshness.Now(), WorkspaceId);

        Assert.Same(overlapping, verdict.Snapshot);
        Assert.Equal(RepositoryFactGroups.Identity | RepositoryFactGroups.CommitCounts, verdict.Ambiguous);
        Assert.Equal(RepositoryFactGroups.None, verdict.Superseded);
    }

    [Theory]
    [InlineData("another-session", 1L, 2L)]
    [InlineData(null, null, null)]
    public void A_snapshot_of_another_Worker_session_or_without_capture_evidence_is_ordered_by_arrival(string? session, long? start, long? end)
    {
        // The Worker restarted: its counter started again, so a small number is not "older".
        var freshness = new RepositoryStateFreshness();
        freshness.RecordApplied(ContextA, Repo, Checkout("aaa", start: 500, end: 501), WorkspaceId);
        var incoming = Checkout("bbb", start, end, session);

        var verdict = freshness.Evaluate(ContextA, Repo, incoming, RepositoryStateFreshness.Now(), WorkspaceId);

        Assert.Same(incoming, verdict.Snapshot);
        Assert.Equal(RepositoryFactGroups.None, verdict.Superseded | verdict.Ambiguous);
    }

    [Fact]
    public void An_older_identity_takes_the_groups_that_describe_that_checkout_with_it_and_leaves_newer_branch_lists()
    {
        var freshness = new RepositoryStateFreshness();
        freshness.RecordApplied(ContextA, Repo, new RepositoryStateSnapshot
        {
            HeadCommit = "bbb", IdentityProbed = true, CaptureSession = Session, CaptureStart = 20, CaptureEnd = 21,
        }, WorkspaceId);
        var older = new RepositoryStateSnapshot
        {
            HeadCommit = "aaa",
            GitVersion = "1.0.0",
            IncomingCommits = 4,
            LocalBranches = ["main"],
            IdentityProbed = true,
            GitVersionProbed = true,
            CommitCountsProbed = true,
            BranchesProbed = true,
            CaptureSession = Session,
            CaptureStart = 10,
            CaptureEnd = 11,
        };

        var verdict = freshness.Evaluate(ContextA, Repo, older, RepositoryStateFreshness.Now(), WorkspaceId);

        // No branch list was applied yet, so that group is the only one left of the older snapshot.
        Assert.Equal(RepositoryFactGroups.Identity | RepositoryFactGroups.GitVersion | RepositoryFactGroups.CommitCounts, verdict.Superseded);
        Assert.NotNull(verdict.Snapshot);
        Assert.True(verdict.Snapshot!.BranchesProbed);
        Assert.False(verdict.Snapshot.IdentityProbed || verdict.Snapshot.GitVersionProbed || verdict.Snapshot.CommitCountsProbed);
        Assert.Null(verdict.Snapshot.HeadCommit);
        Assert.Null(verdict.Snapshot.GitVersion);
        Assert.Equal(["main"], verdict.Snapshot.LocalBranches);
    }

    [Fact]
    public void Branch_lists_are_ordered_across_the_worktrees_of_one_Workspace_and_checkout_facts_are_not()
    {
        // Two worktrees of one repository share its refs. A list read later through the Feature worktree must not be
        // replaced by a list read earlier through the main checkout. What each has checked out is its own.
        var freshness = new RepositoryStateFreshness();
        freshness.RecordApplied(ContextA, Repo, Branches(["main", "feat", "new"], start: 30, end: 31), WorkspaceId);
        freshness.RecordApplied(ContextA, Repo, Checkout("feature-head", start: 30, end: 31), WorkspaceId);
        var olderThroughMainCheckout = new RepositoryStateSnapshot
        {
            HeadCommit = "main-head",
            LocalBranches = ["main", "feat"],
            IdentityProbed = true,
            BranchesProbed = true,
            CaptureSession = Session,
            CaptureStart = 10,
            CaptureEnd = 11,
        };

        var verdict = freshness.Evaluate(ContextB, Repo, olderThroughMainCheckout, RepositoryStateFreshness.Now(), WorkspaceId);

        Assert.Equal(RepositoryFactGroups.Branches, verdict.Superseded);
        Assert.NotNull(verdict.Snapshot);
        Assert.True(verdict.Snapshot!.IdentityProbed);
        Assert.Equal("main-head", verdict.Snapshot.HeadCommit);
        Assert.Null(verdict.Snapshot.LocalBranches);
    }

    [Fact]
    public void An_operation_that_wrote_the_branch_lists_through_one_context_supersedes_an_older_hook_list_of_another()
    {
        var freshness = new RepositoryStateFreshness();
        var hookReceivedAt = RepositoryStateFreshness.Now();
        var operationStartedAt = hookReceivedAt + 1;
        freshness.RecordOperationWrite(ContextA, Repo, Branches(["main", "new"], null, null, session: null), operationStartedAt, WorkspaceId);

        var verdict = freshness.Evaluate(ContextB, Repo, Branches(["main"], null, null, session: null), hookReceivedAt, WorkspaceId);

        Assert.Null(verdict.Snapshot);
        Assert.Equal(RepositoryFactGroups.Branches, verdict.Superseded);
        // Another Workspace's contexts are not affected.
        Assert.NotNull(freshness.Evaluate(ContextB, Repo, Branches(["main"], null, null, session: null), hookReceivedAt, workspaceId: 99).Snapshot);
    }

    [Fact]
    public void Forgetting_a_context_removes_its_evidence()
    {
        var freshness = new RepositoryStateFreshness();
        freshness.RecordApplied(ContextA, Repo, Checkout("bbb", start: 20, end: 21), WorkspaceId);

        freshness.ForgetContext(ContextA);

        // A context id that is used again starts without the old context's order.
        Assert.NotNull(freshness.Evaluate(ContextA, Repo, Checkout("aaa", start: 10, end: 11), RepositoryStateFreshness.Now(), WorkspaceId).Snapshot);
    }

    [Fact]
    public void Forgetting_a_Workspace_removes_the_branch_list_evidence_its_contexts_shared()
    {
        var freshness = new RepositoryStateFreshness();
        freshness.RecordApplied(ContextA, Repo, Branches(["main", "new"], start: 30, end: 31), WorkspaceId);
        Assert.Null(freshness.Evaluate(ContextB, Repo, Branches(["main"], start: 10, end: 11), RepositoryStateFreshness.Now(), WorkspaceId).Snapshot);

        freshness.ForgetWorkspace(WorkspaceId);

        Assert.NotNull(freshness.Evaluate(ContextB, Repo, Branches(["main"], start: 10, end: 11), RepositoryStateFreshness.Now(), WorkspaceId).Snapshot);
    }

    // ---------------------------------------------------------------- through the hook handler and the database

    [Fact]
    public async Task A_hook_snapshot_captured_first_and_delivered_last_does_not_put_the_older_commit_back()
    {
        var reconciler = new RecordingReconciler();
        await using var ctx = await NewContextAsync(reconciler);

        await HandleAsync(ctx, Notification(ctx, Checkout("2222222222222222222222222222222222222222", start: 20, end: 21, branch: "main")));
        var broadcasts = ctx.Broadcasts.Count;
        // Captured before the first one, delivered after it: a slow hook job, a retried send.
        await HandleAsync(ctx, Notification(ctx, Checkout("1111111111111111111111111111111111111111", start: 10, end: 11, branch: "main")));

        Assert.Equal("2222222222222222222222222222222222222222", await ReadHeadAsync(ctx, await ctx.GetSpecialContextIdAsync()));
        // Dropped whole: no state write, so nothing was announced and nothing recomputed.
        Assert.Equal(broadcasts, ctx.Broadcasts.Count);
        Assert.Empty(reconciler.Requests);
    }

    [Fact]
    public async Task A_notification_delivered_twice_is_stored_once()
    {
        await using var ctx = await NewContextAsync(new RecordingReconciler());
        var snapshot = Checkout("2222222222222222222222222222222222222222", start: 20, end: 21, branch: "main");

        await HandleAsync(ctx, Notification(ctx, snapshot));
        var broadcasts = ctx.Broadcasts.Count;
        await HandleAsync(ctx, Notification(ctx, Checkout("2222222222222222222222222222222222222222", start: 20, end: 21, branch: "main")));

        Assert.Equal(broadcasts, ctx.Broadcasts.Count);
    }

    [Fact]
    public async Task Overlapping_captures_are_applied_and_the_repository_is_read_again()
    {
        var reconciler = new RecordingReconciler();
        await using var ctx = await NewContextAsync(reconciler);
        var special = await ctx.GetSpecialContextIdAsync();

        await HandleAsync(ctx, Notification(ctx, Checkout("1111111111111111111111111111111111111111", start: 10, end: 14, branch: "main")));
        await HandleAsync(ctx, Notification(ctx, Checkout("2222222222222222222222222222222222222222", start: 12, end: 16, branch: "main")));

        // Neither is known to be the later one. The second is stored (it may be newer), and one read is asked for.
        Assert.Equal("2222222222222222222222222222222222222222", await ReadHeadAsync(ctx, special));
        var request = Assert.Single(reconciler.Requests);
        Assert.Equal((ctx.WorkspaceId, special.Value, ctx.RepositoryId), (request.WorkspaceId, request.ContextId, request.RepositoryId));
        Assert.Contains("overlapping", request.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_older_branch_list_from_the_main_checkout_does_not_remove_a_branch_a_Feature_worktree_reported_later()
    {
        await using var ctx = await NewContextAsync(new RecordingReconciler());
        var (featureContextId, worktreePath) = await SeedFeatureAsync(ctx);

        // Reported through the Feature worktree, after a branch was created there.
        await HandleAsync(ctx, Notification(ctx, Branches(["main", "feat-evidence", "created-in-feature"], start: 30, end: 31), worktreePath));
        // Read earlier through the main checkout, delivered later.
        await HandleAsync(ctx, Notification(ctx, Branches(["main", "feat-evidence"], start: 10, end: 11)));

        var local = (await ctx.ReadBranchesAsync()).Where(b => !b.IsRemote && !b.IsTag).Select(b => b.BranchName).Order().ToList();
        Assert.Equal(["created-in-feature", "feat-evidence", "main"], local);
        _ = featureContextId;
    }

    [Fact]
    public async Task A_late_notification_from_a_deleted_Feature_writes_nothing_and_brings_nothing_back()
    {
        await using var ctx = await NewContextAsync(new RecordingReconciler());
        var (featureContextId, worktreePath) = await SeedFeatureAsync(ctx);
        var late = Notification(ctx, Checkout("3333333333333333333333333333333333333333", start: 40, end: 41, branch: "feat-evidence"), worktreePath);
        await DeleteFeatureAsync(ctx, featureContextId);
        var broadcasts = ctx.Broadcasts.Count;

        await HandleAsync(ctx, late);

        await using var scope = ctx.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await db.WorkspaceFeatureContexts.AnyAsync(c => c.WorkspaceFeatureContextId == featureContextId.Value));
        Assert.False(await db.WorkspaceRepositoryContextStates.AnyAsync(s => s.WorkspaceFeatureContextId == featureContextId.Value));
        // The path belongs to no context any more: it is not written to the special Workspace either.
        Assert.NotEqual("3333333333333333333333333333333333333333", await ReadHeadAsync(ctx, await ctx.GetSpecialContextIdAsync()));
        Assert.Equal(broadcasts, ctx.Broadcasts.Count);

        // The writer itself refuses a context that is gone, and creates no row for it.
        var written = await scope.ServiceProvider.GetRequiredService<WorkspaceRepositoryStateWriter>().ApplyAsync(
            featureContextId, ctx.WorkspaceId, ctx.RepositoryId, late.State!);
        Assert.False(written.Applied);
        Assert.False(await db.WorkspaceRepositoryContextStates.AnyAsync(s => s.WorkspaceFeatureContextId == featureContextId.Value));
    }

    // ---------------------------------------------------------------- what a write changed

    [Fact]
    public async Task A_write_reports_the_groups_it_changed_and_a_write_that_confirms_the_stored_state_reports_none()
    {
        await using var ctx = await NewContextAsync(new RecordingReconciler());
        var special = await ctx.GetSpecialContextIdAsync();
        await using var scope = ctx.CreateScope();
        var writer = scope.ServiceProvider.GetRequiredService<WorkspaceRepositoryStateWriter>();
        RepositoryStateSnapshot Full(string head, int incoming, params string[] local) => new()
        {
            BranchName = "main",
            HeadCommit = head,
            DefaultBranchName = "main",
            OutgoingCommits = 0,
            IncomingCommits = incoming,
            HasUpstream = true,
            LocalBranches = [.. local],
            RemoteBranches = ["main"],
            Tags = [],
            IdentityProbed = true,
            CommitCountsProbed = true,
            UpstreamProbed = true,
            BranchesProbed = true,
        };

        var first = await writer.ApplyAsync(special, ctx.WorkspaceId, ctx.RepositoryId, Full("1111111111111111111111111111111111111111", 2, "main"));
        var same = await writer.ApplyAsync(special, ctx.WorkspaceId, ctx.RepositoryId, Full("1111111111111111111111111111111111111111", 2, "main"));
        var counts = await writer.ApplyAsync(special, ctx.WorkspaceId, ctx.RepositoryId, Full("1111111111111111111111111111111111111111", 5, "main"));
        var branch = await writer.ApplyAsync(special, ctx.WorkspaceId, ctx.RepositoryId, Full("1111111111111111111111111111111111111111", 5, "main", "topic"));
        var commit = await writer.ApplyAsync(special, ctx.WorkspaceId, ctx.RepositoryId, Full("2222222222222222222222222222222222222222", 5, "main", "topic"));

        Assert.True(first.Applied);
        Assert.True(first.Changed.HasFlag(RepositoryFactGroups.Identity | RepositoryFactGroups.Branches));
        Assert.Equal(RepositoryFactGroups.Identity | RepositoryFactGroups.CommitCounts | RepositoryFactGroups.Upstream | RepositoryFactGroups.Branches, same.Probed);
        Assert.Equal(RepositoryFactGroups.None, same.Changed);
        Assert.Equal(RepositoryFactGroups.CommitCounts, counts.Changed);
        Assert.Equal(RepositoryFactGroups.Branches, branch.Changed);
        Assert.Equal(RepositoryFactGroups.Identity, commit.Changed);
    }

    // ---------------------------------------------------------------- reading again

    [Fact]
    public async Task The_reconciler_queues_a_repository_once_and_reads_it_through_the_single_repository_refresh()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        var special = await ctx.GetSpecialContextIdAsync();
        using var reconciler = new RepositoryStateReconciler(ctx.Resolve<IServiceScopeFactory>(), NullLogger<RepositoryStateReconciler>.Instance);

        Assert.True(reconciler.Request(ctx.WorkspaceId, special, ctx.RepositoryId, "overlapping captures"));
        Assert.False(reconciler.Request(ctx.WorkspaceId, special, ctx.RepositoryId, "overlapping captures"));

        // A read that fails is this request's failure only: nothing is thrown at the queue.
        await reconciler.RunAsync(
            new RepositoryStateReconciler.ReadRequest(ctx.WorkspaceId, special, ctx.RepositoryId, "overlapping captures"), CancellationToken.None);

        var counts = WorkerCommandCounts.Of(ctx.WorkerBridge);
        counts.AssertCount("RefreshRepositoryVersion", 1);
        Assert.Equal(["RefreshRepositoryVersion"], counts.Commands);
    }

    private sealed class RecordingReconciler : IRepositoryStateReconciler
    {
        public List<(int WorkspaceId, int ContextId, int RepositoryId, string Reason)> Requests { get; } = [];

        public bool Request(int workspaceId, WorkspaceFeatureContextId contextId, int repositoryId, string reason)
        {
            lock (Requests)
                Requests.Add((workspaceId, contextId.Value, repositoryId, reason));
            return true;
        }
    }

    private static Task<SyncStateTestContext> NewContextAsync(RecordingReconciler reconciler)
        => SyncStateTestContext.CreateAsync(configureServices: services => services.AddSingleton<IRepositoryStateReconciler>(reconciler));

    private static RepositoryStateSnapshot Checkout(string head, long? start, long? end, string? session = Session, string? branch = null) => new()
    {
        BranchName = branch,
        HeadCommit = head,
        IncomingCommits = 0,
        OutgoingCommits = 0,
        IdentityProbed = true,
        CommitCountsProbed = true,
        CaptureSession = start is null ? null : session,
        CaptureStart = start,
        CaptureEnd = end,
    };

    private static RepositoryStateSnapshot Branches(string[] local, long? start, long? end, string? session = Session) => new()
    {
        LocalBranches = [.. local],
        RemoteBranches = ["main"],
        BranchesProbed = true,
        CaptureSession = start is null ? null : session,
        CaptureStart = start,
        CaptureEnd = end,
    };

    private static RepositorySyncNotification Notification(SyncStateTestContext ctx, RepositoryStateSnapshot snapshot, string? repositoryPath = null) => new()
    {
        WorkspaceId = ctx.WorkspaceId,
        RepositoryId = ctx.RepositoryId,
        RepositoryPath = repositoryPath,
        Branch = snapshot.BranchName ?? "-",
        State = snapshot,
    };

    private static async Task HandleAsync(SyncStateTestContext ctx, RepositorySyncNotification notification)
    {
        await using var scope = ctx.CreateScope();
        await scope.ServiceProvider.GetRequiredService<SyncCommandHandler>().HandleAsync(notification, RepositoryStateFreshness.Now());
    }

    private static async Task<string?> ReadHeadAsync(SyncStateTestContext ctx, WorkspaceFeatureContextId contextId)
    {
        await using var scope = ctx.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.WorkspaceRepositoryContextStates.AsNoTracking()
            .Where(s => s.WorkspaceFeatureContextId == contextId.Value && s.WorkspaceRepositoryId == ctx.WorkspaceRepositoryId)
            .Select(s => s.HeadCommit)
            .FirstOrDefaultAsync();
    }

    private static async Task<(WorkspaceFeatureContextId ContextId, string WorktreePath)> SeedFeatureAsync(SyncStateTestContext ctx)
    {
        const string worktreePath = @"C:\gm-test-root\.graymoon\test-ws\features\feat-evidence\graymoon-api";
        await using var scope = ctx.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var feature = new WorkspaceFeature
        {
            WorkspaceId = ctx.WorkspaceId,
            Name = "feat-evidence",
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
        };
        db.WorkspaceFeatureContexts.Add(context);
        await db.SaveChangesAsync();
        db.WorkspaceFeatureRepositories.Add(new WorkspaceFeatureRepository
        {
            WorkspaceFeatureContextId = context.WorkspaceFeatureContextId,
            WorkspaceRepositoryId = ctx.WorkspaceRepositoryId,
            WorktreePath = worktreePath,
            State = WorkspaceFeatureRepositoryState.Ready,
            BaseCommitSha = "abc123",
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        return (new WorkspaceFeatureContextId(context.WorkspaceFeatureContextId), worktreePath);
    }

    private static async Task DeleteFeatureAsync(SyncStateTestContext ctx, WorkspaceFeatureContextId contextId)
    {
        await using var scope = ctx.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.WorkspaceFeatureRepositories.Where(r => r.WorkspaceFeatureContextId == contextId.Value).ExecuteDeleteAsync();
        await db.WorkspaceRepositoryContextStates.Where(s => s.WorkspaceFeatureContextId == contextId.Value).ExecuteDeleteAsync();
        var context = await db.WorkspaceFeatureContexts.SingleAsync(c => c.WorkspaceFeatureContextId == contextId.Value);
        var feature = await db.WorkspaceFeatures.SingleAsync(f => f.WorkspaceFeatureId == context.WorkspaceFeatureId);
        db.WorkspaceFeatureContexts.Remove(context);
        db.WorkspaceFeatures.Remove(feature);
        await db.SaveChangesAsync();
    }
}
