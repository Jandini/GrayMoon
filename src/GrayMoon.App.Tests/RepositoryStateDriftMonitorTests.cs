using GrayMoon.Abstractions.Notifications;
using GrayMoon.App.Data;
using GrayMoon.App.Hubs;
using GrayMoon.App.Services.GitChanges;
using GrayMoon.App.Services.Workspaces;
using GrayMoon.Application.Features;
using GrayMoon.Common.Git;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace GrayMoon.App.Tests;

/// <summary>
/// A Git hook that is lost (the App or the Worker was down, the hook failed, the connection dropped) left the
/// stored commit and branch behind the checkout until the next Sync: nothing compared them. Every Git Changes
/// status scan, including the periodic audit, reads what is checked out; the drift monitor compares that with the
/// stored state and asks for one read when they still differ after a grace period. Time is a manual clock.
/// </summary>
public sealed class RepositoryStateDriftMonitorTests
{
    private const string Stored = "1111111111111111111111111111111111111111";
    private const string Actual = "2222222222222222222222222222222222222222";

    [Fact]
    public async Task A_missed_commit_is_found_from_one_status_scan_although_no_other_event_follows()
    {
        await using var rig = await Rig.CreateAsync();

        // The audit's scan of an otherwise quiet repository: the only signal there will ever be.
        rig.Observe(Actual, "main");
        Assert.Empty(rig.Reconciler.Requests);

        await rig.PassGraceAsync();

        var request = Assert.Single(rig.Reconciler.Requests);
        Assert.Equal((rig.Ctx.WorkspaceId, rig.Special.Value, rig.Ctx.RepositoryId), (request.WorkspaceId, request.ContextId, request.RepositoryId));
    }

    [Fact]
    public async Task A_hook_that_arrives_within_the_grace_period_means_no_read()
    {
        await using var rig = await Rig.CreateAsync();

        // The status scan is faster than the hook that reports the same commit.
        rig.Observe(Actual, "main");
        await rig.StoreAsync(Actual, "main");
        await rig.PassGraceAsync();

        Assert.Empty(rig.Reconciler.Requests);
    }

    [Fact]
    public async Task A_scan_that_agrees_with_the_stored_state_is_compared_once()
    {
        await using var rig = await Rig.CreateAsync();

        rig.Observe(Stored, "main");
        await rig.PassGraceAsync();
        // The next scans of the same checkout (every file edit sends one) cost nothing.
        rig.Observe(Stored, "main");
        rig.Observe(Stored, "main");

        Assert.False(rig.Monitor.IsWaiting(rig.Special, rig.Ctx.RepositoryId));
        Assert.Empty(rig.Reconciler.Requests);
    }

    [Fact]
    public async Task A_branch_switched_outside_GrayMoon_on_the_same_commit_is_found()
    {
        await using var rig = await Rig.CreateAsync();

        rig.Observe(Stored, "topic");
        await rig.PassGraceAsync();

        Assert.Single(rig.Reconciler.Requests);
    }

    [Fact]
    public async Task The_latest_scan_is_the_one_compared()
    {
        await using var rig = await Rig.CreateAsync();

        // A checkout away and back within the grace period: the checkout is what is stored again.
        rig.Observe(Actual, "topic");
        rig.Observe(Stored, "main");
        await rig.PassGraceAsync();

        Assert.Empty(rig.Reconciler.Requests);
    }

    [Fact]
    public async Task The_same_difference_asks_again_only_after_the_retry_period()
    {
        await using var rig = await Rig.CreateAsync();
        rig.Observe(Actual, "main");
        await rig.PassGraceAsync();
        Assert.Single(rig.Reconciler.Requests);

        // The read did not repair it (the Worker was busy, the read failed). The next audits see the same.
        rig.Observe(Actual, "main");
        await rig.PassGraceAsync();
        Assert.Single(rig.Reconciler.Requests);

        rig.Clock.Advance(RepositoryStateDriftMonitor.RetryAfter);
        rig.Observe(Actual, "main");
        await rig.PassGraceAsync();
        Assert.Equal(2, rig.Reconciler.Requests.Count);
    }

    [Fact]
    public async Task A_checkout_that_an_operation_is_changing_is_compared_when_the_operation_has_ended()
    {
        await using var rig = await Rig.CreateAsync();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(rig.Ctx.Resolve<IWorkspaceOperationLock>().TryStartContext(
            rig.Special, rig.Ctx.WorkspaceId, "Test", $"/workspaces/{rig.Ctx.WorkspaceId}", "Testing", (_, _) => release.Task, out var operation));

        rig.Observe(Actual, "main");
        await rig.PassGraceAsync(untilIdle: false);
        // The operation moves the checkout and reports its own result.
        Assert.Empty(rig.Reconciler.Requests);
        Assert.True(rig.Monitor.IsWaiting(rig.Special, rig.Ctx.RepositoryId));

        release.SetResult();
        await Wait.UntilAsync(() => !rig.Ctx.Resolve<IWorkspaceOperationLock>().IsContextBusy(rig.Special));
        await rig.PassGraceAsync();

        Assert.Single(rig.Reconciler.Requests);
        _ = operation;
    }

    [Fact]
    public async Task A_context_that_was_deleted_while_the_comparison_waited_asks_for_nothing()
    {
        await using var rig = await Rig.CreateAsync();

        rig.Observe(Actual, "main");
        rig.Monitor.ForgetContext(rig.Special);
        await rig.PassGraceAsync();

        Assert.Empty(rig.Reconciler.Requests);
    }

    [Fact]
    public async Task After_the_requested_read_the_stored_state_is_the_checkout_and_the_next_scan_agrees()
    {
        await using var rig = await Rig.CreateAsync();
        rig.Ctx.WorkerBridge.Respond("RefreshRepositoryVersion", new
        {
            version = "-",
            branch = "main",
            commit = Actual,
            outgoingCommits = 1,
            incomingCommits = 0,
            hasUpstream = true,
            defaultBranchBehind = 0,
            defaultBranchAhead = 1,
        });
        rig.Observe(Actual, "main");
        await rig.PassGraceAsync();
        var request = Assert.Single(rig.Reconciler.Requests);

        // What the reconciler does with the request: one read of that one repository.
        using var reconciler = new RepositoryStateReconciler(rig.Ctx.Resolve<IServiceScopeFactory>(), NullLogger<RepositoryStateReconciler>.Instance);
        await reconciler.RunAsync(
            new RepositoryStateReconciler.ReadRequest(request.WorkspaceId, new WorkspaceFeatureContextId(request.ContextId), request.RepositoryId, request.Reason),
            CancellationToken.None);

        Assert.Equal(Actual, await rig.ReadStoredHeadAsync());
        Assert.Equal(["RefreshRepositoryVersion"], WorkerCommandCounts.Of(rig.Ctx.WorkerBridge).Commands);
        rig.Clock.Advance(RepositoryStateDriftMonitor.RetryAfter);
        rig.Observe(Actual, "main");
        await rig.PassGraceAsync();
        Assert.Single(rig.Reconciler.Requests);
    }

    [Theory]
    [InlineData(Stored, "main", false, Stored, "main", true)]
    [InlineData(Actual, "main", false, Stored, "main", false)]
    [InlineData(Stored, "topic", false, Stored, "main", false)]
    // Detached: the branch name of the scan is a placeholder and is not compared.
    [InlineData(Stored, "(detached)", true, Stored, "main", true)]
    // Not known is not a contradiction: an unborn branch, a repository whose commit was never stored.
    [InlineData(null, "main", false, Stored, "main", true)]
    [InlineData(Actual, "main", false, null, "main", true)]
    [InlineData(Actual, "main", false, null, null, true)]
    [InlineData("2222222222222222222222222222222222222222", "main", false, "2222222222222222222222222222222222222222 ", "main", true)]
    public void Only_two_known_and_different_values_are_drift(string? seenHead, string seenBranch, bool detached, string? storedHead, string? storedBranch, bool matches)
        => Assert.Equal(matches, RepositoryStateDriftMonitor.Matches(
            new RepositoryCheckoutObservation(1, new WorkspaceFeatureContextId(1), 1, 1, seenHead, seenBranch, detached), storedHead, storedBranch));

    // ---------------------------------------------------------------- the status scan reports to the monitor

    [Fact]
    public async Task Every_status_snapshot_that_is_not_older_than_the_stored_one_is_reported_also_when_it_changes_nothing()
    {
        await using var ctx = await GitChangesTestDbContext.CreateAsync();
        var monitor = new RecordingMonitor();
        var handler = GitChangesPushHandlerTestFactory.Create(ctx, new FakeHubContext<WorkspaceSyncHub>(), driftMonitor: monitor);
        GitChangesSnapshotNotification Snapshot(long version, string head) => new()
        {
            WorkspaceId = ctx.WorkspaceId,
            RepositoryId = ctx.RepositoryId,
            Snapshot = new GitChangeSnapshot { Version = version, BranchName = "main", HeadCommit = head, Changes = [], ScannedAt = DateTimeOffset.UtcNow },
        };

        await handler.HandleAsync(Snapshot(5, Stored), CancellationToken.None);
        // The periodic audit of an unchanged repository returns the same snapshot version: nothing to store, still an observation.
        await handler.HandleAsync(Snapshot(5, Stored), CancellationToken.None);
        // An older snapshot says nothing about the checkout now.
        await handler.HandleAsync(Snapshot(4, Actual), CancellationToken.None);

        Assert.Equal([Stored, Stored], monitor.Observations.Select(o => o.HeadCommit));
        Assert.All(monitor.Observations, o => Assert.Equal((ctx.WorkspaceId, ctx.RepositoryId, ctx.WorkspaceRepositoryId, "main"), (o.WorkspaceId, o.RepositoryId, o.WorkspaceRepositoryId, o.BranchName)));
    }

    private sealed class RecordingMonitor : IRepositoryStateDriftMonitor
    {
        public List<RepositoryCheckoutObservation> Observations { get; } = [];

        public void Observe(RepositoryCheckoutObservation observation) => Observations.Add(observation);

        public void ForgetContext(WorkspaceFeatureContextId contextId)
        {
        }
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

    /// <summary>A Workspace whose one repository has <see cref="Stored"/> on main stored, and a monitor on a manual clock.</summary>
    private sealed class Rig : IAsyncDisposable
    {
        public required SyncStateTestContext Ctx { get; init; }
        public required ManualClock Clock { get; init; }
        public required RecordingReconciler Reconciler { get; init; }
        public required RepositoryStateDriftMonitor Monitor { get; init; }
        public required WorkspaceFeatureContextId Special { get; init; }

        public static async Task<Rig> CreateAsync()
        {
            var ctx = await SyncStateTestContext.CreateAsync();
            var clock = new ManualClock();
            var reconciler = new RecordingReconciler();
            var rig = new Rig
            {
                Ctx = ctx,
                Clock = clock,
                Reconciler = reconciler,
                Special = await ctx.GetSpecialContextIdAsync(),
                Monitor = new RepositoryStateDriftMonitor(
                    ctx.Resolve<IDbContextFactory<AppDbContext>>(), reconciler, ctx.Resolve<IWorkspaceOperationLock>(), clock,
                    NullLogger<RepositoryStateDriftMonitor>.Instance),
            };
            await rig.StoreAsync(Stored, "main");
            return rig;
        }

        public void Observe(string head, string branch)
            => Monitor.Observe(new RepositoryCheckoutObservation(Ctx.WorkspaceId, Special, Ctx.RepositoryId, Ctx.WorkspaceRepositoryId, head, branch, false));

        /// <summary>Lets the grace period of the waiting comparison pass and, by default, waits for the comparison to finish.</summary>
        public async Task PassGraceAsync(bool untilIdle = true)
        {
            if (Monitor.HasPendingChecks)
            {
                // The comparison arms its timer on a pool thread; time moves only once it is armed.
                await Wait.UntilAsync(() => Clock.NextTimerDelay is not null);
                Clock.Advance(RepositoryStateDriftMonitor.Grace);
            }

            if (untilIdle)
                await Monitor.WhenIdleAsync();
            else
                await Wait.UntilAsync(() => Clock.NextTimerDelay is not null);
        }

        public async Task StoreAsync(string head, string branch)
        {
            await using var scope = Ctx.CreateScope();
            var written = await scope.ServiceProvider.GetRequiredService<WorkspaceRepositoryStateWriter>().ApplyAsync(
                Special, Ctx.WorkspaceId, Ctx.RepositoryId,
                new RepositoryStateSnapshot { BranchName = branch, HeadCommit = head, IdentityProbed = true });
            Assert.True(written.Applied);
        }

        public async Task<string?> ReadStoredHeadAsync()
        {
            await using var db = await Ctx.Resolve<IDbContextFactory<AppDbContext>>().CreateDbContextAsync();
            return await db.WorkspaceRepositoryContextStates.AsNoTracking()
                .Where(s => s.WorkspaceFeatureContextId == Special.Value && s.WorkspaceRepositoryId == Ctx.WorkspaceRepositoryId)
                .Select(s => s.HeadCommit)
                .SingleAsync();
        }

        public async ValueTask DisposeAsync()
        {
            Monitor.Dispose();
            await Ctx.DisposeAsync();
        }
    }
}
