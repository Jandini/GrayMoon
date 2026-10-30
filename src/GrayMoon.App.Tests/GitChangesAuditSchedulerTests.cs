using GrayMoon.App.Services.GitChanges;
using GrayMoon.App.Services.Worker;
using GrayMoon.Application.Features;
using GrayMoon.Common.Git;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GrayMoon.App.Tests;

/// <summary>The audit scheduler: single-flight, coalescing, bounded, time-boxed, and the periodic pass.</summary>
[Trait("Category", "WatcherReliability")]
public sealed class GitChangesAuditSchedulerTests
{
    [Fact]
    public async Task Requests_that_arrive_while_a_fresh_audit_runs_collapse_into_it()
    {
        var rig = new AuditRig(new FakeContextCatalog().With(1, 10));
        var gate = new TaskCompletionSource();
        rig.Scanner.Handler = (_, _, _) => gate.Task;

        rig.Scheduler.Request(1, new WorkspaceFeatureContextId(10), GitChangesAuditReason.WorkerOnline);
        await Wait.UntilAsync(() => rig.Scanner.Calls == 1);
        rig.Scheduler.Request(1, new WorkspaceFeatureContextId(10), GitChangesAuditReason.WorkerOnline);
        rig.Scheduler.Request(1, new WorkspaceFeatureContextId(10), GitChangesAuditReason.WorkerRestarted);
        gate.SetResult();
        await rig.Scheduler.WhenIdleAsync();

        Assert.Equal(1, rig.Scanner.Calls);
    }

    [Fact]
    public async Task A_request_that_arrives_after_the_running_audit_started_long_ago_queues_exactly_one_more()
    {
        var rig = new AuditRig(new FakeContextCatalog().With(1, 10));
        var gate = new TaskCompletionSource();
        rig.Scanner.Handler = (_, _, _) => gate.Task;
        var context = new WorkspaceFeatureContextId(10);

        rig.Scheduler.Request(1, context, GitChangesAuditReason.Periodic);
        await Wait.UntilAsync(() => rig.Scanner.Calls == 1);
        rig.Clock.Advance(TimeSpan.FromSeconds(30));
        rig.Scheduler.Request(1, context, GitChangesAuditReason.WorkerOnline);
        rig.Scheduler.Request(1, context, GitChangesAuditReason.WorkerOnline);
        gate.SetResult();
        await rig.Scheduler.WhenIdleAsync();

        Assert.Equal(2, rig.Scanner.Calls);
    }

    [Fact]
    public async Task Parallelism_is_bounded_across_contexts()
    {
        var rig = new AuditRig(new FakeContextCatalog().With(1, 10).With(1, 11).With(1, 12), maxParallel: 2);
        var gate = new TaskCompletionSource();
        rig.Scanner.Handler = (_, _, _) => gate.Task;

        foreach (var context in new[] { 10, 11, 12 })
        {
            rig.Scheduler.Request(1, new WorkspaceFeatureContextId(context), GitChangesAuditReason.Periodic);
        }

        await Wait.UntilAsync(() => rig.Scanner.Calls == 2);
        await Task.Delay(100);
        Assert.Equal(2, rig.Scanner.Calls);

        gate.SetResult();
        await rig.Scheduler.WhenIdleAsync();
        Assert.Equal(3, rig.Scanner.Calls);
        Assert.Equal(2, rig.Scanner.MaxConcurrent);
    }

    [Fact]
    public async Task A_hung_scan_is_cancelled_by_its_timeout_and_the_next_audit_runs()
    {
        var rig = new AuditRig(new FakeContextCatalog().With(1, 10));
        var context = new WorkspaceFeatureContextId(10);
        rig.Scanner.Handler = (_, _, ct) => Task.Delay(Timeout.Infinite, ct);

        rig.Scheduler.Request(1, context, GitChangesAuditReason.Periodic);
        await Wait.UntilAsync(() => rig.Scanner.Calls == 1);
        rig.Clock.Advance(TimeSpan.FromSeconds(900));
        await rig.Scheduler.WhenIdleAsync();

        rig.Scanner.Handler = (_, _, _) => Task.CompletedTask;
        rig.Scheduler.Request(1, context, GitChangesAuditReason.Periodic);
        await rig.Scheduler.WhenIdleAsync();

        Assert.Equal(2, rig.Scanner.Calls);
    }

    [Fact]
    public async Task A_failed_audit_does_not_prevent_later_audits_and_completion_time_is_recorded()
    {
        var rig = new AuditRig(new FakeContextCatalog().With(1, 10));
        var context = new WorkspaceFeatureContextId(10);
        rig.Scanner.Handler = (_, _, _) => throw new InvalidOperationException("scan failed");

        rig.Scheduler.Request(1, context, GitChangesAuditReason.Periodic);
        await rig.Scheduler.WhenIdleAsync();
        Assert.Equal(rig.Clock.GetUtcNow(), rig.Scheduler.LastCompletedAt(1, context));

        rig.Clock.Advance(TimeSpan.FromMinutes(5));
        rig.Scanner.Handler = (_, _, _) => Task.CompletedTask;
        rig.Scheduler.Request(1, context, GitChangesAuditReason.Periodic);
        await rig.Scheduler.WhenIdleAsync();

        Assert.Equal(2, rig.Scanner.Calls);
        Assert.Equal(rig.Clock.GetUtcNow(), rig.Scheduler.LastCompletedAt(1, context));
    }

    [Fact]
    public async Task Paused_contexts_inactive_workspaces_and_an_offline_Worker_are_not_scanned()
    {
        var rig = new AuditRig(new FakeContextCatalog().With(1, 10).With(1, 11));

        using (rig.Pause.Pause(11))
        {
            rig.Scheduler.Request(1, new WorkspaceFeatureContextId(11), GitChangesAuditReason.Periodic);
            await rig.Scheduler.WhenIdleAsync();
        }

        rig.Scheduler.Request(2, new WorkspaceFeatureContextId(20), GitChangesAuditReason.Periodic);
        await rig.Scheduler.WhenIdleAsync();

        rig.Clock.Advance(TimeSpan.FromMinutes(1));
        rig.Tracker.OnWorkerDisconnected("w");
        rig.Scheduler.Request(1, new WorkspaceFeatureContextId(10), GitChangesAuditReason.Periodic);
        await rig.Scheduler.WhenIdleAsync();

        Assert.Equal(0, rig.Scanner.Calls);
    }

    [Fact]
    public async Task A_periodic_pass_audits_every_unpaused_context_staggered()
    {
        var rig = new AuditRig(new FakeContextCatalog().With(1, 10).With(1, 11).With(1, 12).With(1, 13));
        using var pause = rig.Pause.Pause(13);

        var pass = rig.Scheduler.RunPeriodicPassAsync(CancellationToken.None);
        await Wait.UntilAsync(() => rig.Scanner.Calls == 1);
        await Task.Delay(100);
        Assert.Equal(1, rig.Scanner.Calls);

        await Wait.PumpAsync(rig.Clock, () => rig.Scanner.Calls == 3, GitChangesAuditScheduler.PeriodicStagger);
        await pass.WaitAsync(TimeSpan.FromSeconds(10));
        await rig.Scheduler.WhenIdleAsync();

        Assert.Equal(3, rig.Scanner.Calls);
    }

    [Fact]
    public async Task The_audit_loop_runs_on_the_configured_interval_for_viewed_workspaces_only()
    {
        var rig = new AuditRig(new FakeContextCatalog().With(1, 10).With(2, 20));

        // StartAsync queues the loop. Advance one interval after its timer is armed, then wait on the
        // wall clock. Another advance while that scan is still queued would schedule a second pass.
        await rig.Scheduler.StartAsync(CancellationToken.None);
        try
        {
            await Wait.UntilAsync(() => rig.Clock.NextTimerDelay == rig.AuditInterval);
            rig.Clock.Advance(rig.AuditInterval);
            await Wait.UntilAsync(() => rig.Scanner.Calls >= 1);
            await rig.Scheduler.WhenIdleAsync();
            Assert.Equal(1, rig.Scanner.Calls);
        }
        finally
        {
            await rig.Scheduler.StopAsync(CancellationToken.None);
        }
    }

    private sealed class AuditRig
    {
        public AuditRig(FakeContextCatalog catalog, int maxParallel = 2)
        {
            Tracker.OnWorkerConnected("w");
            Lease = Activity.Subscribe(1);
            var gitChangesOptions = new GitChangesOptions { WatcherAuditMaxParallelContexts = maxParallel };
            AuditInterval = TimeSpan.FromMinutes(Math.Max(1, gitChangesOptions.WatcherAuditIntervalMinutes));
            Scheduler = new GitChangesAuditScheduler(
                Scanner, Activity, Pause, Tracker, catalog,
                Options.Create(gitChangesOptions),
                Clock, NullLogger<GitChangesAuditScheduler>.Instance);
        }

        public TimeSpan AuditInterval { get; }

        public IDisposable Lease { get; }
        public ManualClock Clock { get; } = new();
        public WorkerConnectionTracker Tracker { get; } = new("1.0.0");
        public WorkspaceGitChangesActivityTracker Activity { get; } = new(Options.Create(new GitChangesOptions()));
        public WorkspaceGitChangesMonitoringPause Pause { get; } = new();
        public ControlledScanner Scanner { get; } = new();
        public GitChangesAuditScheduler Scheduler { get; }
    }

    private sealed class ControlledScanner : IGitChangesWorkspaceScanner
    {
        private int _calls;
        private int _current;
        private int _max;

        public Func<int, int, CancellationToken, Task> Handler { get; set; } = (_, _, _) => Task.CompletedTask;

        public int Calls => Volatile.Read(ref _calls);

        public int MaxConcurrent => Volatile.Read(ref _max);

        public async Task ScanWorkspaceAsync(
            int workspaceId,
            WorkspaceFeatureContextId contextId,
            CancellationToken cancellationToken,
            Action<GitChangesWorkspaceScanProgress>? onProgress = null,
            bool includeLineStats = false,
            int? repositoryId = null,
            bool persistImmediately = false)
        {
            Interlocked.Increment(ref _calls);
            var now = Interlocked.Increment(ref _current);
            int seen;
            while (now > (seen = Volatile.Read(ref _max)) && Interlocked.CompareExchange(ref _max, now, seen) != seen)
            {
            }

            try
            {
                await Handler(workspaceId, contextId.Value, cancellationToken);
            }
            finally
            {
                Interlocked.Decrement(ref _current);
            }
        }
    }
}
