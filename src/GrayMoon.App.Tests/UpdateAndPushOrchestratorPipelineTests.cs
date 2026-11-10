using System.Threading.Channels;
using GrayMoon.Application.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace GrayMoon.App.Tests;

/// <summary>
/// The pipeline wiring of <see cref="UpdateAndPushOrchestrator"/> with fake lanes: hand-off order across a three level
/// chain, overlap of the two lanes, and how a failing lane settles the other. The work inside each lane is covered by
/// the update and push strategy tests.
/// </summary>
public sealed class UpdateAndPushOrchestratorPipelineTests
{
    private const int WorkspaceId = 4;
    private static readonly WorkspaceFeatureContextId Context = new(9);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private static DependencyLevelCompletion Level(int level)
        => new(level, new HashSet<int> { level }, new HashSet<int> { level }, new HashSet<int> { level });

    private static UpdateAndPushOrchestrator Create(FakeUpdateLane update, FakePushLane push)
    {
        var services = new ServiceCollection().AddSingleton<IPushLane>(push).BuildServiceProvider();
        return new UpdateAndPushOrchestrator(update, push, services.GetRequiredService<IServiceScopeFactory>(), NullLogger<UpdateAndPushOrchestrator>.Instance);
    }

    private static Task<UpdateAndPushResult> Run(UpdateAndPushOrchestrator orchestrator, Action<int, string>? levelError = null)
        => orchestrator.RunAsync(WorkspaceId, Context, CancellationToken.None, _ => { }, (_, _) => { }, levelError ?? ((_, _) => { }));

    [Fact]
    public async Task Three_levels_are_pushed_in_order_and_level_one_goes_out_while_level_three_is_still_updating()
    {
        var push = new FakePushLane();
        var update = new FakeUpdateLane
        {
            Levels = [1, 2, 3],
            // Level 3 only finishes once the push lane has taken level 1: proves the lanes overlap.
            BeforeLevel = async level => { if (level == 3) await push.Received(1).WaitAsync(Timeout); },
        };

        var result = await Run(Create(update, push)).WaitAsync(Timeout);

        Assert.True(result.Pipelined);
        Assert.True(result.Success);
        Assert.Equal([1, 2, 3], push.ReceivedLevels);
        Assert.Equal(3, result.PushedRepoCount);
        Assert.Equal([1, 2, 3], update.CompletedLevels);
    }

    [Fact]
    public async Task Nothing_runs_when_the_push_lane_cannot_start()
    {
        var push = new FakePushLane { CanRun = false };
        var update = new FakeUpdateLane { Levels = [1, 2, 3] };

        var result = await Run(Create(update, push));

        Assert.False(result.Pipelined);
        Assert.Equal(0, update.RunCount);
        Assert.Equal(0, push.RunCount);
    }

    [Fact]
    public async Task A_failed_update_is_reported_and_everything_already_handed_over_is_still_pushed()
    {
        var push = new FakePushLane();
        var update = new FakeUpdateLane { Levels = [1, 2], FailAfterLevels = true };

        var result = await Run(Create(update, push)).WaitAsync(Timeout);

        Assert.True(result.Pipelined);
        Assert.False(result.Update.Success);
        Assert.False(result.Success);
        Assert.Equal([1, 2], push.ReceivedLevels);
        Assert.Equal(2, result.PushedRepoCount);
    }

    [Fact]
    public async Task A_push_failure_stops_the_update_at_its_next_level_boundary_and_names_what_was_not_pushed()
    {
        // #11: the update used to commit levels 3 and 4 although nothing would push them.
        var push = new FakePushLane { FailLevel = 2 };
        var update = new FakeUpdateLane
        {
            Levels = [1, 2, 3, 4],
            // Level 3 is being updated while level 2 fails to push; the stop lands on the level 3 boundary.
            BeforeLevel = async level => { if (level == 3) await push.Ended.WaitAsync(Timeout); },
        };
        var reported = new List<(int Level, string Message)>();

        var result = await Run(Create(update, push), (level, message) => { lock (reported) reported.Add((level, message)); }).WaitAsync(Timeout);

        Assert.Equal([1, 2, 3], update.CompletedLevels);
        Assert.Equal([1, 2], push.ReceivedLevels);
        Assert.True(result.Update.Success);
        Assert.NotNull(result.Push);
        Assert.False(result.Push!.Success);
        Assert.False(result.Success);
        const string expected = "Level 2 failed to push. Level 3 is committed locally but not pushed. The update stopped after level 3.";
        Assert.Equal("Push stopped at level 2: " + expected, result.Push.Error);
        Assert.Equal(expected, result.Push.LevelErrors![2]);
        Assert.Equal("Push stopped at level 2: " + expected, PushOperationResult.Describe(result.Push));
        Assert.Equal((2, expected), reported[^1]);
    }

    [Fact]
    public async Task A_push_failure_after_the_update_finished_names_the_levels_left_unpushed()
    {
        // The lane fails level 1 only after every level was handed over: nothing is left to stop, but 2 and 3 stay local.
        var handedOver = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var push = new FakePushLane { FailLevel = 1, BeforeLevel = _ => handedOver.Task };
        var update = new FakeUpdateLane { Levels = [1, 2, 3], AfterLevels = handedOver.SetResult };

        var result = await Run(Create(update, push)).WaitAsync(Timeout);

        Assert.Equal([1, 2, 3], update.CompletedLevels);
        Assert.False(result.Push!.Success);
        Assert.Equal(
            "Push stopped at level 1: Level 1 failed to push. Levels 2, 3 are committed locally but not pushed.",
            result.Push.Error);
    }

    [Fact]
    public async Task Restore_warnings_do_not_stop_the_update_and_are_not_reported_as_a_stopped_push()
    {
        var push = new FakePushLane { RestoreWarningLevel = 1 };
        var update = new FakeUpdateLane
        {
            Levels = [1, 2, 3],
            BeforeLevel = async level => { if (level == 2) await push.Received(1).WaitAsync(Timeout); },
        };

        var result = await Run(Create(update, push)).WaitAsync(Timeout);

        Assert.Equal([1, 2, 3], update.CompletedLevels);
        Assert.Equal([1, 2, 3], push.ReceivedLevels);
        Assert.Equal(3, result.PushedRepoCount);
        Assert.Equal("Push finished, but package restore failed in 1 repository.", result.Push!.Error);
        Assert.Null(result.Push.LevelErrors);
        Assert.Equal("Package restore failed for repo-1: error NU1101", result.Push.RepoErrors![1]);
    }

    [Fact]
    public async Task An_update_exception_settles_the_push_lane_then_surfaces()
    {
        var push = new FakePushLane();
        var update = new FakeUpdateLane { Levels = [1], ThrowAfterLevels = new InvalidOperationException("update blew up") };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(Create(update, push)).WaitAsync(Timeout));

        Assert.Equal("update blew up", ex.Message);
        Assert.Equal([1], push.ReceivedLevels);
        Assert.True(push.Finished);
    }

    [Fact]
    public async Task A_push_lane_exception_stops_the_update_and_becomes_a_failed_push_naming_the_level()
    {
        // #11: the exception used to surface only after levels 3 and 4 were committed, with no level in the message.
        var push = new FakePushLane { ThrowAtLevel = 2, ThrowOnRun = new InvalidOperationException("worker not connected") };
        var update = new FakeUpdateLane
        {
            Levels = [1, 2, 3, 4],
            BeforeLevel = async level => { if (level == 3) await push.Ended.WaitAsync(Timeout); },
        };
        var reported = new List<(int Level, string Message)>();

        var result = await Run(Create(update, push), (level, message) => { lock (reported) reported.Add((level, message)); }).WaitAsync(Timeout);

        Assert.Equal([1, 2, 3], update.CompletedLevels);
        Assert.True(result.Update.Success);
        Assert.False(result.Push!.Success);
        const string expected = "worker not connected Level 3 is committed locally but not pushed. The update stopped after level 3.";
        Assert.Equal("Push stopped at level 2: " + expected, result.Push.Error);
        Assert.Equal("Push stopped at level 2: " + expected, PushOperationResult.Describe(result.Push));
        Assert.Equal((2, expected), Assert.Single(reported));
    }

    [Fact]
    public async Task A_push_lane_exception_after_the_update_finished_is_a_failed_push_not_an_exception()
    {
        var push = new FakePushLane { ThrowOnRun = new InvalidOperationException("worker not connected") };
        var update = new FakeUpdateLane { Levels = [1, 2] };

        var result = await Run(Create(update, push)).WaitAsync(Timeout);

        Assert.Equal([1, 2], update.CompletedLevels);
        Assert.False(result.Push!.Success);
        Assert.Equal("Push stopped at level 2: worker not connected", result.Push.Error);
    }

    [Fact]
    public async Task A_cancelled_push_lane_still_surfaces_as_cancellation()
    {
        var push = new FakePushLane { ThrowOnRun = new OperationCanceledException() };
        var update = new FakeUpdateLane { Levels = [1] };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Run(Create(update, push)).WaitAsync(Timeout));
    }

    private sealed class FakeUpdateLane : IDependencyUpdateLane
    {
        public int[] Levels { get; init; } = [];
        public Func<int, Task>? BeforeLevel { get; init; }
        public bool FailAfterLevels { get; init; }
        public Exception? ThrowAfterLevels { get; init; }
        public Action? AfterLevels { get; init; }
        public int RunCount { get; private set; }
        public List<int> CompletedLevels { get; } = [];

        public async Task<DependencyUpdateRunResult> RunAsync(
            int workspaceId, WorkspaceFeatureContextId contextId, CancellationToken cancellationToken, IProgress<OperationProgress>? progress,
            Action<int, string> onRepoError, Action<int, string> onLevelError, Action? onAppSideComplete = null,
            IReadOnlySet<int>? repoIdsToUpdate = null, string? commitMessage = null, bool includeDepsInCommitMessage = true,
            int? maxLevel = null, string? runId = null, Func<DependencyLevelCompletion, Task>? onLevelCompleted = null)
        {
            RunCount++;
            foreach (var level in Levels)
            {
                if (BeforeLevel != null)
                    await BeforeLevel(level);
                CompletedLevels.Add(level);
                if (onLevelCompleted != null)
                    await onLevelCompleted(Level(level));
            }

            AfterLevels?.Invoke();
            if (ThrowAfterLevels != null)
                throw ThrowAfterLevels;

            return FailAfterLevels ? DependencyUpdateRunResult.Failed() : DependencyUpdateRunResult.Ok(Levels.ToHashSet());
        }
    }

    private sealed class FakePushLane : IPushLane
    {
        private readonly Dictionary<int, TaskCompletionSource> _received = [];
        private readonly object _gate = new();

        public bool CanRun { get; init; } = true;
        public int? FailLevel { get; init; }
        public Exception? ThrowOnRun { get; init; }

        /// <summary>Throws <see cref="ThrowOnRun"/> while handling this level; when null it is thrown after the last level.</summary>
        public int? ThrowAtLevel { get; init; }

        /// <summary>Reports a package restore warning for this level and carries on, like the real lane.</summary>
        public int? RestoreWarningLevel { get; init; }

        public Func<int, Task>? BeforeLevel { get; init; }
        public int RunCount { get; private set; }
        public bool Finished { get; private set; }
        public List<int> ReceivedLevels { get; } = [];

        private readonly TaskCompletionSource _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Completes when the lane has returned or thrown.</summary>
        public Task Ended => _ended.Task;

        public Task Received(int level)
        {
            lock (_gate)
            {
                if (!_received.TryGetValue(level, out var tcs))
                    _received[level] = tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                return tcs.Task;
            }
        }

        public Task<bool> CanRunPushLaneAsync(int workspaceId, WorkspaceFeatureContextId contextId, int? maxLevel, CancellationToken cancellationToken)
            => Task.FromResult(CanRun);

        public async Task<int> RunPushLaneAsync(
            int workspaceId, WorkspaceFeatureContextId contextId, ChannelReader<DependencyLevelCompletion> completedLevels,
            Action<string>? onProgressMessage = null, Action<int, string>? onRepoError = null, Action<int, string>? onLevelError = null,
            bool restorePackages = true, string? runId = null, CancellationToken cancellationToken = default)
        {
            RunCount++;
            try
            {
                var pushed = 0;
                await foreach (var completion in completedLevels.ReadAllAsync(cancellationToken))
                {
                    ReceivedLevels.Add(completion.Level);
                    _ = Received(completion.Level);
                    lock (_gate)
                        _received[completion.Level].TrySetResult();
                    if (BeforeLevel != null)
                        await BeforeLevel(completion.Level);

                    if (ThrowAtLevel == completion.Level)
                        throw ThrowOnRun!;

                    // Like the real lane: a failed level is reported, then the lane returns without reading further.
                    if (FailLevel == completion.Level)
                    {
                        onLevelError?.Invoke(completion.Level, $"Level {completion.Level} failed to push.");
                        Finished = true;
                        return pushed;
                    }

                    if (RestoreWarningLevel == completion.Level)
                        onRepoError?.Invoke(completion.Level, $"Package restore failed for repo-{completion.Level}: error NU1101");

                    pushed += completion.RepoIds.Count;
                }

                Finished = true;
                if (ThrowOnRun != null && ThrowAtLevel == null)
                    throw ThrowOnRun;
                return pushed;
            }
            finally
            {
                _ended.TrySetResult();
            }
        }
    }
}
