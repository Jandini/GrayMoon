using GrayMoon.App.Services.GitChanges;
using GrayMoon.App.Services.Worker;
using GrayMoon.Application.Features;
using GrayMoon.Common.Git;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GrayMoon.App.Tests;

/// <summary>The Git-free keeper: cadence, isolation, reconnect and Worker restart handling, on a manual clock.</summary>
[Trait("Category", "WatcherReliability")]
public sealed class GitChangesWatcherKeeperTests
{
    [Fact]
    public async Task An_open_workspace_with_no_interaction_for_hours_keeps_renewing_every_context()
    {
        var rig = new KeeperRig(new FakeContextCatalog().With(1, 10).With(1, 11));
        using var lease = rig.Activity.Subscribe(1);

        // Starting the keeper while the Worker is online runs one reconnect pass (Ensure, then audit); settle it first.
        await rig.Keeper.StartAsync(CancellationToken.None);
        await Wait.UntilAsync(() => rig.Ensurer.Calls.Count == 2 && rig.Audit.Requests.Count == 2);
        await rig.Keeper.WhenReconnectIdleAsync();
        rig.Ensurer.Clear();
        rig.Audit.Clear();

        // 40 ticks of 3 minutes is two hours without a single page interaction; presence is the circuit lease only.
        for (var tick = 1; tick <= 40; tick++)
        {
            rig.Clock.Advance(TimeSpan.FromMinutes(3));
            await Wait.UntilAsync(() => rig.Ensurer.Calls.Count == tick * 2);
        }

        Assert.Empty(rig.Audit.Requests);
        await rig.Keeper.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task One_long_suspend_is_reported_late_once_and_not_on_every_later_tick()
    {
        var logger = new CapturingLogger<GitChangesWatcherKeeper>();
        var rig = new KeeperRig(new FakeContextCatalog().With(1, 10), logger);
        using var lease = rig.Activity.Subscribe(1);
        await rig.Keeper.StartAsync(CancellationToken.None);
        await Wait.UntilAsync(() => rig.Ensurer.Calls.Count == 1);
        await rig.Keeper.WhenReconnectIdleAsync();
        rig.Ensurer.Clear();

        // Sleep and resume: one hour passes in a single step.
        rig.Clock.Jump(TimeSpan.FromMinutes(60));
        await Wait.UntilAsync(() => rig.Ensurer.Calls.Count == 1);
        var lateAfterResume = logger.Entries.Count(e => e.Message.Contains("keeper tick late"));
        Assert.Equal(1, lateAfterResume);

        for (var tick = 2; tick <= 4; tick++)
        {
            rig.Clock.Advance(TimeSpan.FromMinutes(3));
            await Wait.UntilAsync(() => rig.Ensurer.Calls.Count == tick);
        }

        Assert.Equal(lateAfterResume, logger.Entries.Count(e => e.Message.Contains("keeper tick late")));
        await rig.Keeper.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task A_workspace_nobody_is_viewing_is_not_renewed()
    {
        var rig = new KeeperRig(new FakeContextCatalog().With(1, 10));

        await rig.Keeper.RunTickAsync(GitChangesKeeperTickReason.Scheduled, null, CancellationToken.None);

        Assert.Empty(rig.Ensurer.Calls);
    }

    [Fact]
    public async Task A_hung_call_times_out_without_delaying_other_contexts_or_the_next_tick()
    {
        var rig = new KeeperRig(new FakeContextCatalog().With(1, 10).With(1, 11));
        using var lease = rig.Activity.Subscribe(1);
        var hang = true;
        rig.Ensurer.Handler = async (_, context, ct) =>
        {
            if (hang && context == 10)
            {
                await Task.Delay(Timeout.Infinite, ct);
            }

            return KeeperRig.Ok("w1");
        };

        var tick = rig.Keeper.RunTickAsync(GitChangesKeeperTickReason.Scheduled, null, CancellationToken.None);
        await Wait.UntilAsync(() => rig.Ensurer.Calls.Count == 2);

        // Context 11 finished at once; the tick itself is only waiting for context 10's timeout.
        Assert.False(tick.IsCompleted);
        rig.Clock.Advance(TimeSpan.FromSeconds(30));
        await tick.WaitAsync(TimeSpan.FromSeconds(10));

        hang = false;
        await rig.Keeper.RunTickAsync(GitChangesKeeperTickReason.Scheduled, null, CancellationToken.None);
        Assert.Equal(4, rig.Ensurer.Calls.Count);
        Assert.Empty(rig.Audit.Requests);
    }

    [Fact]
    public async Task A_context_that_throws_or_fails_does_not_stop_the_others()
    {
        var rig = new KeeperRig(new FakeContextCatalog().With(1, 10).With(1, 11).With(1, 12));
        using var lease = rig.Activity.Subscribe(1);
        rig.Ensurer.Handler = (_, context, _) => context switch
        {
            10 => throw new InvalidOperationException("boom"),
            11 => Task.FromResult(new GitChangesEnsureResult { Success = false, ErrorMessage = "Worker not connected" }),
            _ => Task.FromResult(KeeperRig.Ok("w1")),
        };

        await rig.Keeper.RunTickAsync(GitChangesKeeperTickReason.Scheduled, null, CancellationToken.None);

        Assert.Equal([10, 11, 12], rig.Ensurer.Calls.Select(c => c.ContextId).Order());
        Assert.Empty(rig.Audit.Requests);
    }

    [Fact]
    public async Task A_context_paused_for_removal_is_not_renewed_but_the_Workspace_context_is()
    {
        var rig = new KeeperRig(new FakeContextCatalog().With(1, 10).With(1, 11));
        using var lease = rig.Activity.Subscribe(1);
        using var pause = rig.Pause.Pause(11);

        await rig.Keeper.RunTickAsync(GitChangesKeeperTickReason.Scheduled, null, CancellationToken.None);

        Assert.Equal([10], rig.Ensurer.Calls.Select(c => c.ContextId));
    }

    [Fact]
    public async Task A_double_Online_runs_exactly_one_Ensure_and_one_audit_per_context()
    {
        var rig = new KeeperRig(new FakeContextCatalog().With(1, 10).With(1, 11));
        using var lease = rig.Activity.Subscribe(1);

        rig.Keeper.HandleWorkerState(WorkerConnectionState.Online);
        rig.Clock.Advance(TimeSpan.FromMilliseconds(40));
        rig.Keeper.HandleWorkerState(WorkerConnectionState.Online);
        rig.Clock.Advance(TimeSpan.FromMilliseconds(50));
        // The shape seen live: the state flips through VersionMismatch and back inside the same reconnect.
        rig.Keeper.HandleWorkerState(WorkerConnectionState.VersionMismatch);
        rig.Keeper.HandleWorkerState(WorkerConnectionState.Online);
        await rig.Keeper.WhenReconnectIdleAsync();

        Assert.Equal([10, 11], rig.Ensurer.Calls.Select(c => c.ContextId).Order());
        Assert.Equal(
            [(1, 10, GitChangesAuditReason.WorkerOnline), (1, 11, GitChangesAuditReason.WorkerOnline)],
            rig.Audit.Requests.OrderBy(r => r.ContextId));
    }

    [Fact]
    public async Task A_later_real_reconnect_runs_again()
    {
        var rig = new KeeperRig(new FakeContextCatalog().With(1, 10));
        using var lease = rig.Activity.Subscribe(1);

        rig.Keeper.HandleWorkerState(WorkerConnectionState.Online);
        await rig.Keeper.WhenReconnectIdleAsync();
        rig.Clock.Advance(TimeSpan.FromMinutes(1));
        rig.Keeper.HandleWorkerState(WorkerConnectionState.Offline);
        rig.Keeper.HandleWorkerState(WorkerConnectionState.Online);
        await rig.Keeper.WhenReconnectIdleAsync();

        Assert.Equal(2, rig.Ensurer.Calls.Count);
        Assert.Equal(2, rig.Audit.Requests.Count);
    }

    [Fact]
    public async Task A_changed_Worker_instance_requests_an_audit_even_without_an_Online_event()
    {
        var rig = new KeeperRig(new FakeContextCatalog().With(1, 10).With(1, 11));
        using var lease = rig.Activity.Subscribe(1);
        var instance = "a";
        rig.Ensurer.Handler = (_, _, _) => Task.FromResult(KeeperRig.Ok(instance));

        await rig.Keeper.RunTickAsync(GitChangesKeeperTickReason.Scheduled, null, CancellationToken.None);
        Assert.Empty(rig.Audit.Requests);

        instance = "b";
        await rig.Keeper.RunTickAsync(GitChangesKeeperTickReason.Scheduled, null, CancellationToken.None);
        Assert.Equal(
            [(1, 10, GitChangesAuditReason.WorkerRestarted), (1, 11, GitChangesAuditReason.WorkerRestarted)],
            rig.Audit.Requests.OrderBy(r => r.ContextId));

        rig.Audit.Clear();
        await rig.Keeper.RunTickAsync(GitChangesKeeperTickReason.Scheduled, null, CancellationToken.None);
        Assert.Empty(rig.Audit.Requests);
    }

    [Fact]
    public async Task Nothing_is_sent_while_the_Worker_is_offline()
    {
        var rig = new KeeperRig(new FakeContextCatalog().With(1, 10));
        using var lease = rig.Activity.Subscribe(1);
        rig.Tracker.OnWorkerDisconnected("w");

        await rig.Keeper.RunTickAsync(GitChangesKeeperTickReason.Scheduled, null, CancellationToken.None);

        Assert.Empty(rig.Ensurer.Calls);
    }

    private sealed class KeeperRig
    {
        public KeeperRig(FakeContextCatalog catalog, Microsoft.Extensions.Logging.ILogger<GitChangesWatcherKeeper>? logger = null)
        {
            Tracker.OnWorkerConnected("w");
            Keeper = new GitChangesWatcherKeeper(
                Ensurer, Audit, Activity, Pause, Tracker, catalog,
                Options.Create(new GitChangesOptions()), Clock, logger ?? NullLogger<GitChangesWatcherKeeper>.Instance);
        }

        public ManualClock Clock { get; } = new();
        public WorkerConnectionTracker Tracker { get; } = new("1.0.0");
        public WorkspaceGitChangesActivityTracker Activity { get; } = new(Options.Create(new GitChangesOptions()));
        public WorkspaceGitChangesMonitoringPause Pause { get; } = new();
        public RecordingAuditScheduler Audit { get; } = new();
        public FakeEnsurer Ensurer { get; } = new();
        public GitChangesWatcherKeeper Keeper { get; }

        public static GitChangesEnsureResult Ok(string instance) => new()
        {
            Success = true,
            WorkerInstanceId = instance,
            EffectiveGraceSeconds = 600,
            Results = [new GitChangesEnsureRepositoryResult { RepositoryId = 1, Status = "Renewed" }],
        };
    }

    private sealed class FakeEnsurer : IGitChangesContextEnsurer
    {
        private readonly List<(int WorkspaceId, int ContextId)> _calls = [];

        public Func<int, int, CancellationToken, Task<GitChangesEnsureResult>> Handler { get; set; } =
            (_, _, _) => Task.FromResult(KeeperRig.Ok("w1"));

        public IReadOnlyList<(int WorkspaceId, int ContextId)> Calls
        {
            get
            {
                lock (_calls)
                {
                    return [.. _calls];
                }
            }
        }

        public void Clear()
        {
            lock (_calls)
            {
                _calls.Clear();
            }
        }

        public Task<GitChangesEnsureResult> EnsureAsync(int workspaceId, WorkspaceFeatureContextId contextId, CancellationToken cancellationToken)
        {
            lock (_calls)
            {
                _calls.Add((workspaceId, contextId.Value));
            }

            return Handler(workspaceId, contextId.Value, cancellationToken);
        }
    }
}

internal static class Wait
{
    public static async Task UntilAsync(Func<bool> condition, int timeoutSeconds = 20)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Condition was not met in time.");
            }

            await Task.Delay(5);
        }
    }

    /// <summary>Advances the clock in small steps until the condition holds; for waits that register their timer late.</summary>
    public static async Task PumpAsync(ManualClock clock, Func<bool> condition, TimeSpan step)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Condition was not met in time.");
            }

            clock.Advance(step);
            await Task.Delay(5);
        }
    }
}
