using GrayMoon.Application.Features;

namespace GrayMoon.App.Tests;

public sealed class UpdateAndPushApprovalStoreTests
{
    private static readonly WorkspaceFeatureContextId Context = new(3);

    [Fact]
    public void Consume_returns_the_pending_push_once()
    {
        var store = new UpdateAndPushApprovalStore();
        var id = store.Add("run", 1, Context, maxLevel: 2, restorePackages: true, new HashSet<int> { 4 }, new HashSet<int> { 5 });

        var pending = store.TryConsume(1, Context, id);

        Assert.NotNull(pending);
        Assert.Equal("run", pending!.RunId);
        Assert.Equal(2, pending.MaxLevel);
        Assert.Equal([4], pending.RepositoryIds);
        Assert.Null(store.TryConsume(1, Context, id));
    }

    [Fact]
    public void Consume_for_another_workspace_or_context_does_not_use_up_the_approval()
    {
        var store = new UpdateAndPushApprovalStore();
        var id = store.Add("run", 1, Context, null, false, new HashSet<int>(), new HashSet<int>());

        Assert.Null(store.TryConsume(2, Context, id));
        Assert.Null(store.TryConsume(1, new WorkspaceFeatureContextId(99), id));
        Assert.NotNull(store.TryConsume(1, Context, id));
    }

    [Fact]
    public void A_newer_request_for_the_same_context_replaces_the_older_approval()
    {
        var store = new UpdateAndPushApprovalStore();
        var first = store.Add("run-1", 1, Context, null, false, new HashSet<int>(), new HashSet<int>());
        var second = store.Add("run-2", 1, Context, null, false, new HashSet<int>(), new HashSet<int>());

        Assert.Null(store.TryConsume(1, Context, first));
        Assert.Equal("run-2", store.TryConsume(1, Context, second)!.RunId);
    }

    [Fact]
    public void Expired_approval_is_refused()
    {
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 10, 27, 12, 0, 0, TimeSpan.Zero));
        var store = new UpdateAndPushApprovalStore(clock);
        var id = store.Add("run", 1, Context, null, false, new HashSet<int>(), new HashSet<int>());

        clock.Now += UpdateAndPushApprovalStore.Lifetime + TimeSpan.FromSeconds(1);

        Assert.Null(store.TryConsume(1, Context, id));
    }

    private sealed class MutableTimeProvider(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
