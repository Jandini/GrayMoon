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
    public async Task A_push_failure_is_reported_while_the_update_still_walks_every_level()
    {
        var push = new FakePushLane { FailLevel = 2 };
        var update = new FakeUpdateLane { Levels = [1, 2, 3] };
        var reported = new List<(int Level, string Message)>();

        var result = await Run(Create(update, push), (level, message) => reported.Add((level, message))).WaitAsync(Timeout);

        Assert.True(result.Update.Success);
        Assert.Equal([1, 2, 3], update.CompletedLevels);
        Assert.NotNull(result.Push);
        Assert.False(result.Push!.Success);
        Assert.Contains(reported, e => e.Level == 2);
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
    public async Task A_push_lane_exception_surfaces_after_the_update_finished()
    {
        var push = new FakePushLane { ThrowOnRun = new InvalidOperationException("worker not connected") };
        var update = new FakeUpdateLane { Levels = [1, 2] };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(Create(update, push)).WaitAsync(Timeout));

        Assert.Equal("worker not connected", ex.Message);
        Assert.Equal([1, 2], update.CompletedLevels);
    }

    private sealed class FakeUpdateLane : IDependencyUpdateLane
    {
        public int[] Levels { get; init; } = [];
        public Func<int, Task>? BeforeLevel { get; init; }
        public bool FailAfterLevels { get; init; }
        public Exception? ThrowAfterLevels { get; init; }
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
        public int RunCount { get; private set; }
        public bool Finished { get; private set; }
        public List<int> ReceivedLevels { get; } = [];

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
            var pushed = 0;
            var failed = false;
            await foreach (var completion in completedLevels.ReadAllAsync(cancellationToken))
            {
                ReceivedLevels.Add(completion.Level);
                _ = Received(completion.Level);
                lock (_gate)
                    _received[completion.Level].TrySetResult();

                if (failed)
                    continue;

                if (FailLevel == completion.Level)
                {
                    failed = true;
                    onLevelError?.Invoke(completion.Level, $"Level {completion.Level} failed to push.");
                    continue;
                }

                pushed += completion.RepoIds.Count;
            }

            Finished = true;
            if (ThrowOnRun != null)
                throw ThrowOnRun;
            return pushed;
        }
    }
}
