using GrayMoon.Worker.Abstractions;
using GrayMoon.Worker.Jobs;
using GrayMoon.Worker.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace GrayMoon.Worker.Tests;

public sealed class WorktreeCreateHookDeferralTests
{
    private static readonly string Path1 = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "gm-defer", "wt1");

    private sealed class RecordingQueue : IJobQueue
    {
        public List<JobEnvelope> Jobs { get; } = [];

        public ValueTask EnqueueAsync(JobEnvelope job, CancellationToken cancellationToken = default)
        {
            lock (Jobs) Jobs.Add(job);
            return ValueTask.CompletedTask;
        }

        public IAsyncEnumerable<JobEnvelope> ReadAllAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public int Count { get { lock (Jobs) return Jobs.Count; } }
    }

    private static NotifySyncJob Job(string path, NotifyHookKind kind) =>
        new() { RepositoryId = 1, WorkspaceId = 1, RepositoryPath = path, HookKind = kind };

    private static WorktreeCreateHookDeferral Create(RecordingQueue queue) =>
        new(queue, NullLogger<WorktreeCreateHookDeferral>.Instance, TimeSpan.FromMilliseconds(50));

    [Fact]
    public void Checkout_for_a_worktree_not_being_created_is_not_deferred()
    {
        using var sut = Create(new RecordingQueue());
        Assert.False(sut.TryDefer(Job(Path1, NotifyHookKind.Checkout)));
    }

    [Fact]
    public void Other_hook_kinds_are_never_deferred()
    {
        using var sut = Create(new RecordingQueue());
        using var scope = sut.BeginCreate(Path1);
        Assert.False(sut.TryDefer(Job(Path1, NotifyHookKind.Commit)));
    }

    [Fact]
    public async Task Deferred_checkout_is_released_once_after_the_create_ends()
    {
        var queue = new RecordingQueue();
        using var sut = Create(queue);
        var scope = sut.BeginCreate(Path1);

        Assert.True(sut.TryDefer(Job(Path1, NotifyHookKind.Checkout)));
        Assert.True(sut.TryDefer(Job(Path1.ToUpperInvariant(), NotifyHookKind.Checkout)) || !OperatingSystem.IsWindows());
        Assert.Equal(0, queue.Count);

        scope.Dispose();
        scope.Dispose();

        for (var i = 0; i < 100 && queue.Count == 0; i++)
            await Task.Delay(20);

        Assert.True(queue.Count >= 1);
        Assert.All(queue.Jobs, j => Assert.Equal(JobKind.Notify, j.Kind));
    }

    [Fact]
    public async Task Release_waits_while_any_create_is_still_running()
    {
        var queue = new RecordingQueue();
        using var sut = Create(queue);
        var first = sut.BeginCreate(Path1);
        var second = sut.BeginCreate(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "gm-defer", "wt2"));

        Assert.True(sut.TryDefer(Job(Path1, NotifyHookKind.Checkout)));
        first.Dispose();
        await Task.Delay(300);
        Assert.Equal(0, queue.Count);

        second.Dispose();
        for (var i = 0; i < 100 && queue.Count == 0; i++)
            await Task.Delay(20);
        Assert.Equal(1, queue.Count);
    }

    [Fact]
    public async Task Released_checkout_is_marked_as_a_fresh_worktree()
    {
        var queue = new RecordingQueue();
        using var sut = Create(queue);
        var scope = sut.BeginCreate(Path1);

        Assert.True(sut.TryDefer(Job(Path1, NotifyHookKind.Checkout)));
        scope.Dispose();

        for (var i = 0; i < 100 && queue.Count == 0; i++)
            await Task.Delay(20);

        var released = Assert.IsType<NotifySyncJob>(Assert.Single(queue.Jobs).NotifyJob);
        Assert.True(released.FreshWorktree);
        Assert.Equal(NotifyHookKind.Checkout, released.HookKind);
    }
}
