using GrayMoon.Worker.Abstractions;
using GrayMoon.Worker.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace GrayMoon.Worker.Tests;

[Trait("Category", "PullRequest")]
public sealed class RepositoryAccessTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "gm-access-tests");
    private static readonly string Feature = Path.Combine(Root, "feature");
    private static readonly string RepoA = Path.Combine(Feature, "RepoA");
    private static readonly string RepoB = Path.Combine(Feature, "RepoB");
    private static readonly string Other = Path.Combine(Root, "other");

    private static readonly RepositoryAccessTimings Fast =
        new(TimeSpan.FromMilliseconds(150), TimeSpan.FromMilliseconds(400), TimeSpan.FromMilliseconds(100));

    private readonly RepositoryAccess _access = new(NullLogger<RepositoryAccess>.Instance);

    [Fact]
    public async Task Shared_lease_is_granted_without_a_claim_and_refused_inside_one()
    {
        using var free = _access.TryAcquireShared(RepoA, RepositoryAccessKind.ReadOnly);
        Assert.NotNull(free);
        free!.Dispose();

        using var claim = await _access.AcquireExclusiveAsync([RepoA], CancellationToken.None, Fast);

        Assert.Null(_access.TryAcquireShared(RepoA, RepositoryAccessKind.ReadOnly));
        Assert.Null(_access.TryAcquireShared(Path.Combine(RepoA, "src", "deep"), RepositoryAccessKind.Mutating));
        Assert.NotNull(_access.TryAcquireShared(RepoB, RepositoryAccessKind.ReadOnly));
        Assert.True(_access.IsUnderRemoval(RepoA));
        Assert.False(_access.IsUnderRemoval(RepoB));
    }

    [Fact]
    public async Task A_claim_on_a_child_does_not_block_a_lease_on_its_parent_but_a_claim_on_the_parent_blocks_children()
    {
        using (await _access.AcquireExclusiveAsync([RepoA], CancellationToken.None, Fast))
        {
            using var parent = _access.TryAcquireShared(Feature, RepositoryAccessKind.ReadOnly);
            Assert.NotNull(parent);
        }

        using (await _access.AcquireExclusiveAsync([Feature], CancellationToken.None, Fast))
        {
            Assert.Null(_access.TryAcquireShared(RepoA, RepositoryAccessKind.ReadOnly));
            Assert.Null(_access.TryAcquireShared(Feature, RepositoryAccessKind.ReadOnly));
        }
    }

    [Fact]
    public async Task Path_matching_is_case_insensitive_and_does_not_confuse_sibling_prefixes()
    {
        using var claim = await _access.AcquireExclusiveAsync([RepoA], CancellationToken.None, Fast);

        Assert.Null(_access.TryAcquireShared(RepoA.ToUpperInvariant(), RepositoryAccessKind.ReadOnly));
        Assert.NotNull(_access.TryAcquireShared(RepoA + "-sibling", RepositoryAccessKind.ReadOnly));
    }

    [Fact]
    public async Task Disposing_the_scope_lifts_the_claim_and_overlapping_scopes_are_independent()
    {
        var first = await _access.AcquireExclusiveAsync([RepoA], CancellationToken.None, Fast);
        var second = await _access.AcquireExclusiveAsync([RepoA], CancellationToken.None, Fast);

        first.Dispose();
        first.Dispose();
        Assert.True(_access.IsUnderRemoval(RepoA));
        second.Dispose();
        Assert.False(_access.IsUnderRemoval(RepoA));
        Assert.NotNull(_access.TryAcquireShared(RepoA, RepositoryAccessKind.ReadOnly));
    }

    [Fact]
    public async Task Read_only_holder_is_cancelled_at_once_and_the_claim_returns_when_it_lets_go()
    {
        var lease = _access.TryAcquireShared(RepoA, RepositoryAccessKind.ReadOnly)!;
        var holder = Task.Run(async () =>
        {
            try { await Task.Delay(Timeout.Infinite, lease.Yield); }
            catch (OperationCanceledException) { /* expected */ }
            finally { lease.Dispose(); }
        });

        var started = DateTime.UtcNow;
        using var claim = await _access.AcquireExclusiveAsync([RepoA], CancellationToken.None, new RepositoryAccessTimings(TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(1)));

        await holder.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(10), "a reader must not be given the grace period");
    }

    [Fact]
    public async Task Mutating_holder_gets_the_grace_period_then_is_cancelled()
    {
        var lease = _access.TryAcquireShared(RepoA, RepositoryAccessKind.Mutating)!;
        var cancelledAt = (DateTime?)null;
        var holder = Task.Run(async () =>
        {
            try { await Task.Delay(Timeout.Infinite, lease.Yield); }
            catch (OperationCanceledException) { cancelledAt = DateTime.UtcNow; }
            finally { lease.Dispose(); }
        });

        var started = DateTime.UtcNow;
        using var claim = await _access.AcquireExclusiveAsync([RepoA], CancellationToken.None, Fast);
        await holder;

        Assert.NotNull(cancelledAt);
        Assert.True(cancelledAt!.Value - started >= Fast.MutatingGrace - TimeSpan.FromMilliseconds(30));
    }

    [Fact]
    public async Task Mutating_holder_that_finishes_inside_the_grace_period_is_never_cancelled()
    {
        var lease = _access.TryAcquireShared(RepoA, RepositoryAccessKind.Mutating)!;
        _ = Task.Run(async () =>
        {
            await Task.Delay(40);
            lease.Dispose();
        });

        using var claim = await _access.AcquireExclusiveAsync([RepoA], CancellationToken.None, Fast);

        Assert.False(lease.Yield.IsCancellationRequested);
    }

    [Fact]
    public async Task Holder_that_ignores_cancellation_has_its_process_terminated_and_the_claim_still_succeeds()
    {
        var terminated = 0;
        IRepositoryAccessLease? lease = null;
        lease = _access.TryAcquireShared(RepoA, RepositoryAccessKind.Mutating, forceTerminate: () =>
        {
            Interlocked.Increment(ref terminated);
            lease!.Dispose();
        })!;

        using var claim = await _access.AcquireExclusiveAsync([RepoA], CancellationToken.None, Fast);

        Assert.Equal(1, terminated);
        Assert.True(lease.Yield.IsCancellationRequested);
    }

    [Fact]
    public async Task Holder_that_survives_termination_makes_the_claim_throw_and_withdraw()
    {
        using var stuck = _access.TryAcquireShared(RepoA, RepositoryAccessKind.Mutating, forceTerminate: () => { });

        await Assert.ThrowsAsync<RepositoryAccessException>(() =>
            _access.AcquireExclusiveAsync([RepoA], CancellationToken.None, Fast));

        Assert.False(_access.IsUnderRemoval(RepoA));
        Assert.NotNull(_access.TryAcquireShared(RepoB, RepositoryAccessKind.ReadOnly));
    }

    [Fact]
    public async Task Holders_outside_the_claimed_trees_are_left_alone()
    {
        var unrelated = _access.TryAcquireShared(Other, RepositoryAccessKind.Mutating)!;

        using var claim = await _access.AcquireExclusiveAsync([RepoA], CancellationToken.None, Fast);

        Assert.False(unrelated.Yield.IsCancellationRequested);
        unrelated.Dispose();
    }

    [Fact]
    public async Task Owner_can_work_inside_its_own_claim_without_the_claim_waiting_for_it()
    {
        using var claim = await _access.AcquireExclusiveAsync([RepoA], CancellationToken.None, Fast);

        using var inside = _access.TryAcquireShared(RepoA, RepositoryAccessKind.Mutating, owner: claim);
        Assert.NotNull(inside);

        using var second = await _access.AcquireExclusiveAsync([RepoA], CancellationToken.None, Fast);
        Assert.NotNull(second);
    }

    [Fact]
    public async Task Another_owner_is_still_refused_inside_a_claim()
    {
        using var first = await _access.AcquireExclusiveAsync([RepoA], CancellationToken.None, Fast);
        using var second = await _access.AcquireExclusiveAsync([RepoB], CancellationToken.None, Fast);

        Assert.Null(_access.TryAcquireShared(RepoA, RepositoryAccessKind.ReadOnly, owner: second));
    }

    [Fact]
    public async Task Releasables_at_or_beneath_the_claim_are_released_once_and_others_are_not()
    {
        var inside = new CountingReleasable();
        var insideChild = new CountingReleasable();
        var outside = new CountingReleasable();
        _access.RegisterReleasable(RepoA, inside);
        _access.RegisterReleasable(Path.Combine(RepoA, "nested"), insideChild);
        _access.RegisterReleasable(RepoB, outside);

        using (await _access.AcquireExclusiveAsync([RepoA], CancellationToken.None, Fast))
        {
        }

        using (await _access.AcquireExclusiveAsync([RepoA], CancellationToken.None, Fast))
        {
        }

        Assert.Equal(1, inside.Calls);
        Assert.Equal(1, insideChild.Calls);
        Assert.Equal(0, outside.Calls);
    }

    [Fact]
    public async Task Unregistered_releasable_is_not_released()
    {
        var releasable = new CountingReleasable();
        _access.RegisterReleasable(RepoA, releasable).Dispose();

        using var claim = await _access.AcquireExclusiveAsync([RepoA], CancellationToken.None, Fast);

        Assert.Equal(0, releasable.Calls);
    }

    [Fact]
    public async Task Releasable_registered_into_a_claimed_folder_is_released_immediately()
    {
        using var claim = await _access.AcquireExclusiveAsync([RepoA], CancellationToken.None, Fast);
        var late = new CountingReleasable();

        _access.RegisterReleasable(RepoA, late);

        Assert.Equal(1, late.Calls);
    }

    [Fact]
    public async Task A_releasable_that_throws_does_not_stop_the_others_or_the_claim()
    {
        var throwing = new CountingReleasable { Throw = true };
        var fine = new CountingReleasable();
        _access.RegisterReleasable(RepoA, throwing);
        _access.RegisterReleasable(Path.Combine(RepoA, "x"), fine);

        using var claim = await _access.AcquireExclusiveAsync([RepoA], CancellationToken.None, Fast);

        Assert.Equal(1, throwing.Calls);
        Assert.Equal(1, fine.Calls);
    }

    [Fact]
    public async Task Ancestor_observer_hears_start_and_end_for_claims_strictly_beneath_it()
    {
        var observer = new CountingObserver();
        _access.RegisterAncestorObserver(Feature, observer);

        var scope = await _access.AcquireExclusiveAsync([RepoA], CancellationToken.None, Fast);
        Assert.Equal(1, observer.Started);
        Assert.Equal(0, observer.Ended);
        scope.Dispose();
        Assert.Equal(1, observer.Ended);

        using (await _access.AcquireExclusiveAsync([Feature], CancellationToken.None, Fast))
        {
        }

        using (await _access.AcquireExclusiveAsync([Other], CancellationToken.None, Fast))
        {
        }

        Assert.Equal(1, observer.Started);
    }

    [Fact]
    public async Task Observer_registered_during_a_claim_is_told_and_unregistered_observer_gets_no_end()
    {
        var scope = await _access.AcquireExclusiveAsync([RepoA], CancellationToken.None, Fast);
        var late = new CountingObserver();
        var registration = _access.RegisterAncestorObserver(Feature, late);
        Assert.Equal(1, late.Started);

        registration.Dispose();
        scope.Dispose();

        Assert.Equal(0, late.Ended);
    }

    [Fact]
    public async Task Cancelling_the_wait_withdraws_the_claim()
    {
        using var stuck = _access.TryAcquireShared(RepoA, RepositoryAccessKind.Mutating);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        var slow = new RepositoryAccessTimings(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(1));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _access.AcquireExclusiveAsync([RepoA], cts.Token, slow));

        Assert.False(_access.IsUnderRemoval(RepoA));
    }

    [Fact]
    public async Task Many_concurrent_acquire_and_claim_cycles_leave_nothing_behind()
    {
        var holders = Enumerable.Range(0, 200).Select(i => Task.Run(async () =>
        {
            for (var n = 0; n < 20; n++)
            {
                using var lease = _access.TryAcquireShared(RepoA, i % 2 == 0 ? RepositoryAccessKind.ReadOnly : RepositoryAccessKind.Mutating);
                if (lease is null)
                {
                    await Task.Yield();
                    continue;
                }

                try { await Task.Delay(1, lease.Yield); }
                catch (OperationCanceledException) { /* evicted */ }
            }
        })).ToList();

        for (var n = 0; n < 5; n++)
        {
            using var claim = await _access.AcquireExclusiveAsync([RepoA], CancellationToken.None, Fast);
            Assert.Null(_access.TryAcquireShared(RepoA, RepositoryAccessKind.ReadOnly));
        }

        await Task.WhenAll(holders);
        Assert.False(_access.IsUnderRemoval(RepoA));
        using var finalLease = _access.TryAcquireShared(RepoA, RepositoryAccessKind.ReadOnly);
        Assert.NotNull(finalLease);
    }

    private sealed class CountingReleasable : IReleasable
    {
        public int Calls;
        public bool Throw;

        public void Release()
        {
            Interlocked.Increment(ref Calls);
            if (Throw)
            {
                throw new InvalidOperationException("boom");
            }
        }
    }

    private sealed class CountingObserver : IRepositoryAccessObserver
    {
        public int Started;
        public int Ended;

        public void ClaimBeneathStarted() => Interlocked.Increment(ref Started);

        public void ClaimBeneathEnded() => Interlocked.Increment(ref Ended);
    }
}
