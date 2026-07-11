using GrayMoon.Agent.Services.GitChanges;
using GrayMoon.Common.Git;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GrayMoon.Agent.Tests;

public sealed class GitRepositoryWatcherManagerTests : IDisposable
{
    private readonly string _tempDir = Directory.CreateTempSubdirectory("graymoon-watcher-test-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, true);
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    private static GitStatusRefreshCoordinator CreateCoordinator(FakeRepositoryGitChangesService fake, GitChangesOptions options) =>
        new(fake, new GitChangesSnapshotCache(), Options.Create(options), NullLogger<GitStatusRefreshCoordinator>.Instance);

    private static GitRepositoryWatcherManager CreateManager(GitStatusRefreshCoordinator coordinator, GitChangesOptions options) =>
        new(coordinator, Options.Create(options), NullLoggerFactory.Instance, NullLogger<GitRepositoryWatcherManager>.Instance);

    [Fact]
    public void Acquiring_the_same_repository_twice_creates_only_one_watcher()
    {
        var fake = new FakeRepositoryGitChangesService();
        var options = new GitChangesOptions();
        using var coordinator = CreateCoordinator(fake, options);
        using var manager = CreateManager(coordinator, options);

        using var lease1 = manager.Acquire(_tempDir);
        using var lease2 = manager.Acquire(_tempDir);

        Assert.Equal(1, manager.ActiveWatcherCount);
    }

    [Fact]
    public void Acquiring_different_repositories_creates_a_watcher_each()
    {
        var otherDir = Directory.CreateTempSubdirectory("graymoon-watcher-test-2-").FullName;
        try
        {
            var fake = new FakeRepositoryGitChangesService();
            var options = new GitChangesOptions();
            using var coordinator = CreateCoordinator(fake, options);
            using var manager = CreateManager(coordinator, options);

            using var lease1 = manager.Acquire(_tempDir);
            using var lease2 = manager.Acquire(otherDir);

            Assert.Equal(2, manager.ActiveWatcherCount);
        }
        finally
        {
            try { Directory.Delete(otherDir, true); } catch { }
        }
    }

    [Fact]
    public void Releasing_all_leases_does_not_immediately_remove_the_watcher()
    {
        var fake = new FakeRepositoryGitChangesService();
        var options = new GitChangesOptions();
        using var coordinator = CreateCoordinator(fake, options);
        using var manager = CreateManager(coordinator, options);

        var lease = manager.Acquire(_tempDir);
        lease.Dispose();

        // Still alive for the idle grace period rather than torn down the instant the last lease releases.
        Assert.Equal(1, manager.ActiveWatcherCount);
    }

    [Fact]
    public void Dispose_removes_all_watchers_immediately_regardless_of_grace_period()
    {
        var fake = new FakeRepositoryGitChangesService();
        var options = new GitChangesOptions();
        using var coordinator = CreateCoordinator(fake, options);
        var manager = CreateManager(coordinator, options);

        using var lease = manager.Acquire(_tempDir);
        manager.Dispose();

        Assert.Equal(0, manager.ActiveWatcherCount);
    }

    [Fact]
    public async Task File_change_in_a_leased_repository_triggers_a_status_scan()
    {
        var fake = new FakeRepositoryGitChangesService { Delay = TimeSpan.Zero };
        var options = new GitChangesOptions { WatcherDebounceMilliseconds = 50 };
        using var coordinator = CreateCoordinator(fake, options);
        using var manager = CreateManager(coordinator, options);

        using var lease = manager.Acquire(_tempDir);

        await File.WriteAllTextAsync(Path.Combine(_tempDir, "file.txt"), "hello");

        var sawScan = await WaitForAsync(() => fake.CallCount > 0, TimeSpan.FromSeconds(5));

        Assert.True(sawScan, "Expected a file change to trigger a debounced status scan.");
    }

    [Fact]
    public async Task File_change_after_the_last_lease_releases_is_still_observed_during_the_grace_period()
    {
        var fake = new FakeRepositoryGitChangesService { Delay = TimeSpan.Zero };
        var options = new GitChangesOptions { WatcherDebounceMilliseconds = 50 };
        using var coordinator = CreateCoordinator(fake, options);
        using var manager = CreateManager(coordinator, options);

        var lease = manager.Acquire(_tempDir);
        lease.Dispose(); // released, but watcher stays alive during the (>= 1 minute) idle grace period

        await File.WriteAllTextAsync(Path.Combine(_tempDir, "file.txt"), "hello");

        var sawScan = await WaitForAsync(() => fake.CallCount > 0, TimeSpan.FromSeconds(5));

        Assert.True(sawScan, "Expected the watcher to remain active during the idle grace period after release.");
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
