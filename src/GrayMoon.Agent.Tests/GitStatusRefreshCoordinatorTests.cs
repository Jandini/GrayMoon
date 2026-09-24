using GrayMoon.Agent.Services.GitChanges;
using GrayMoon.Common.Git;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GrayMoon.Agent.Tests;

public class GitStatusRefreshCoordinatorTests
{
    private static GitStatusRefreshCoordinator CreateCoordinator(FakeRepositoryGitChangesService fake, GitChangesOptions? options = null) =>
        new(
            fake,
            new GitChangesSnapshotCache(),
            Options.Create(options ?? new GitChangesOptions()),
            NullLogger<GitStatusRefreshCoordinator>.Instance);

    [Fact]
    public async Task RefreshNowAsync_returns_a_successful_snapshot()
    {
        var fake = new FakeRepositoryGitChangesService { Delay = TimeSpan.Zero };
        using var coordinator = CreateCoordinator(fake);

        var result = await coordinator.RefreshNowAsync(@"C:\repo-a", CancellationToken.None);

        Assert.True(result.Success);
        Assert.NotNull(result.Snapshot);
        Assert.Equal(1, fake.CallCount);
    }

    [Fact]
    public async Task Concurrent_refreshes_for_32_repositories_never_exceed_the_configured_parallelism()
    {
        var fake = new FakeRepositoryGitChangesService { Delay = TimeSpan.FromMilliseconds(75) };
        var options = new GitChangesOptions { MaxParallelRepositoryOperations = 4 };
        using var coordinator = CreateCoordinator(fake, options);

        var repoPaths = Enumerable.Range(0, 32).Select(i => $@"C:\repos\repo-{i}").ToList();

        var tasks = repoPaths.Select(path => coordinator.RefreshNowAsync(path, CancellationToken.None));
        var results = await Task.WhenAll(tasks);

        Assert.All(results, r => Assert.True(r.Success));
        Assert.Equal(32, fake.CallCount);
        Assert.True(fake.MaxConcurrentCalls <= 4, $"Observed {fake.MaxConcurrentCalls} concurrent scans, expected at most 4.");
        Assert.True(fake.MaxConcurrentCalls > 1, "Expected different repositories to scan concurrently, not serially.");
    }

    [Fact]
    public async Task The_same_repository_never_scans_concurrently()
    {
        var fake = new FakeRepositoryGitChangesService { Delay = TimeSpan.FromMilliseconds(75) };
        using var coordinator = CreateCoordinator(fake);
        const string repoPath = @"C:\repo-same";

        var tasks = Enumerable.Range(0, 8).Select(_ => coordinator.RefreshNowAsync(repoPath, CancellationToken.None));
        await Task.WhenAll(tasks);

        Assert.Equal(1, fake.MaxConcurrentCallsForRepo(repoPath));
    }

    [Fact]
    public async Task Different_repositories_scan_concurrently()
    {
        var fake = new FakeRepositoryGitChangesService { Delay = TimeSpan.FromMilliseconds(100) };
        using var coordinator = CreateCoordinator(fake);

        var task1 = coordinator.RefreshNowAsync(@"C:\repo-x", CancellationToken.None);
        var task2 = coordinator.RefreshNowAsync(@"C:\repo-y", CancellationToken.None);
        await Task.WhenAll(task1, task2);

        Assert.True(fake.MaxConcurrentCalls >= 2);
    }

    [Fact]
    public async Task MarkDirty_debounces_repeated_events_into_a_single_scan()
    {
        var fake = new FakeRepositoryGitChangesService { Delay = TimeSpan.FromMilliseconds(10) };
        // Debounce window must outlast the quiet period after the last event. Resetting the timer on
        // every Dirty MarkDirty (true trailing debounce) keeps a long burst as one scan even when the
        // burst itself is slower than the window (common on loaded CI agents).
        var options = new GitChangesOptions { WatcherDebounceMilliseconds = 150 };
        using var coordinator = CreateCoordinator(fake, options);
        const string repoPath = @"C:\repo-debounce";

        for (var i = 0; i < 10; i++)
        {
            coordinator.MarkDirty(repoPath);
        }

        var sawScan = await WaitForAsync(() => fake.CallCount >= 1, TimeSpan.FromSeconds(3));
        await Task.Delay(300); // quiet period longer than the debounce window

        Assert.True(sawScan);
        Assert.Equal(1, fake.CallCount);
    }

    [Fact]
    public async Task MarkDirty_during_an_active_scan_triggers_exactly_one_follow_up_scan()
    {
        var fake = new FakeRepositoryGitChangesService { Delay = TimeSpan.FromMilliseconds(200) };
        var options = new GitChangesOptions { WatcherDebounceMilliseconds = 20 };
        using var coordinator = CreateCoordinator(fake, options);
        const string repoPath = @"C:\repo-followup";

        var refreshTask = coordinator.RefreshNowAsync(repoPath, CancellationToken.None);
        await Task.Delay(50); // ensure the scan above is actually in flight
        coordinator.MarkDirty(repoPath);
        coordinator.MarkDirty(repoPath);
        coordinator.MarkDirty(repoPath);

        await refreshTask;

        var sawFollowUp = await WaitForAsync(() => fake.CallCount >= 2, TimeSpan.FromSeconds(3));
        await Task.Delay(300);

        Assert.True(sawFollowUp);
        Assert.Equal(2, fake.CallCount);
    }

    [Fact]
    public async Task Coalesced_caller_receives_the_follow_up_scan_not_the_stale_in_flight_scan()
    {
        var fake = new FakeRepositoryGitChangesService { Delay = TimeSpan.FromMilliseconds(150) };
        using var coordinator = CreateCoordinator(fake);
        const string repoPath = @"C:\repo-coalesce";

        var firstScan = coordinator.RefreshNowAsync(repoPath, CancellationToken.None);
        await Task.Delay(30); // ensure the first scan is actually in flight (Refreshing, not Clean)

        // Arrives while scan #1 is still running - coalesces (RefreshingAndDirty) rather than starting a
        // third scan, but must still be satisfied by a scan that started at or after this call, not by
        // scan #1's already-in-flight (and therefore potentially pre-change) result.
        var coalescedScan = coordinator.RefreshNowAsync(repoPath, CancellationToken.None);

        var firstResult = await firstScan;
        var coalescedResult = await coalescedScan;

        Assert.Equal(2, fake.CallCount);
        var observedVersions = fake.ObservedVersions.ToArray();
        Assert.Equal(2, observedVersions.Length);

        // Both callers must be satisfied by the follow-up (second) scan - never by the stale first scan
        // that was already in flight when the coalesced caller arrived.
        Assert.Equal(observedVersions[1], firstResult.Snapshot!.Version);
        Assert.Equal(observedVersions[1], coalescedResult.Snapshot!.Version);
        Assert.NotEqual(observedVersions[0], coalescedResult.Snapshot!.Version);
    }

    [Fact]
    public async Task Manual_refresh_promotes_a_pending_debounced_scan_to_run_immediately()
    {
        var fake = new FakeRepositoryGitChangesService { Delay = TimeSpan.FromMilliseconds(10) };
        var options = new GitChangesOptions { WatcherDebounceMilliseconds = 5000 }; // long debounce
        using var coordinator = CreateCoordinator(fake, options);
        const string repoPath = @"C:\repo-manual";

        coordinator.MarkDirty(repoPath); // would not fire for 5s if left to the debounce timer
        var result = await coordinator.RefreshNowAsync(repoPath, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(1, fake.CallCount);
    }

    [Fact]
    public async Task Cancelled_owner_scan_does_not_leave_repository_stuck()
    {
        var fake = new FakeRepositoryGitChangesService { Delay = TimeSpan.FromMilliseconds(500) };
        using var coordinator = CreateCoordinator(fake);
        const string repoPath = @"C:\repo-cancel-stuck";

        using var ownerCts = new CancellationTokenSource();
        var owner = coordinator.RefreshNowAsync(repoPath, ownerCts.Token);
        await Task.Delay(50); // ensure the scan is in flight (Refreshing)
        ownerCts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => owner);

        // Without AbortRefresh the tracker stayed Refreshing forever and this hung until process death.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var result = await coordinator.RefreshNowAsync(repoPath, timeout.Token);

        Assert.True(result.Success);
        Assert.Equal(RepositoryRefreshState.Clean, coordinator.GetState(repoPath));
    }

    [Fact]
    public async Task Cancel_while_dirty_unblocks_coalesced_waiters_and_allows_later_refresh()
    {
        var fake = new FakeRepositoryGitChangesService { Delay = TimeSpan.FromMilliseconds(400) };
        var options = new GitChangesOptions { WatcherDebounceMilliseconds = 20 };
        using var coordinator = CreateCoordinator(fake, options);
        const string repoPath = @"C:\repo-cancel-dirty";

        using var ownerCts = new CancellationTokenSource();
        var owner = coordinator.RefreshNowAsync(repoPath, ownerCts.Token);
        await Task.Delay(40);
        coordinator.MarkDirty(repoPath); // RefreshingAndDirty + coalesced pending completion

        using var coalescedCts = new CancellationTokenSource();
        var coalesced = coordinator.RefreshNowAsync(repoPath, coalescedCts.Token);

        ownerCts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => owner);

        // Coalesced waiter must be completed (canceled) by AbortRefresh, not hang.
        using var coalesceWait = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => coalesced.WaitAsync(coalesceWait.Token));

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var result = await coordinator.RefreshNowAsync(repoPath, timeout.Token);
        Assert.True(result.Success);
    }

    [Fact]
    public async Task Coalesced_caller_cancellation_does_not_abort_owner_scan()
    {
        var fake = new FakeRepositoryGitChangesService { Delay = TimeSpan.FromMilliseconds(250) };
        using var coordinator = CreateCoordinator(fake);
        const string repoPath = @"C:\repo-coalesce-cancel";

        var owner = coordinator.RefreshNowAsync(repoPath, CancellationToken.None);
        await Task.Delay(30);

        using var coalescedCts = new CancellationTokenSource();
        var coalesced = coordinator.RefreshNowAsync(repoPath, coalescedCts.Token);
        coalescedCts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => coalesced);
        var ownerResult = await owner.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(ownerResult.Success);
        Assert.True(fake.CallCount >= 1);
    }

    private static async Task<bool> WaitForAsync(Func<bool> condition, TimeSpan timeout)
    {
        var start = DateTime.UtcNow;
        while (DateTime.UtcNow - start < timeout)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(25);
        }

        return condition();
    }
}
