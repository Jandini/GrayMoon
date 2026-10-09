using GrayMoon.Common.Git;
using GrayMoon.Worker.Services.GitChanges;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GrayMoon.Worker.Tests;

[Trait("Category", "PullRequest")]
public sealed class RepositoryPathReleaserTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("graymoon-release-").FullName;
    private readonly GitChangesOptions _options = new() { WatcherDebounceMilliseconds = 20 };
    private readonly RepositoryPathGate _gate = new();
    private readonly FakeRepositoryGitChangesService _fake = new() { Delay = TimeSpan.Zero };
    private readonly GitStatusRefreshCoordinator _coordinator;
    private readonly GitRepositoryWatcherManager _manager;
    private readonly RepositoryPathReleaser _releaser;

    public RepositoryPathReleaserTests()
    {
        _coordinator = new GitStatusRefreshCoordinator(
            _fake, new GitChangesSnapshotCache(), Options.Create(_options), NullLogger<GitStatusRefreshCoordinator>.Instance, _gate);
        _manager = new GitRepositoryWatcherManager(
            _coordinator, new GitChangesSnapshotCache(), new GitChangesRepositoryRegistry(),
            Options.Create(_options), NullLoggerFactory.Instance, NullLogger<GitRepositoryWatcherManager>.Instance, _gate);
        _releaser = new RepositoryPathReleaser(_gate, _manager, _coordinator, NullLogger<RepositoryPathReleaser>.Instance);
    }

    public void Dispose()
    {
        _manager.Dispose();
        _coordinator.Dispose();
        try { Directory.Delete(_root, true); } catch { /* best-effort */ }
    }

    private string MakeDir(string name) => Directory.CreateDirectory(Path.Combine(_root, name)).FullName;

    [Fact]
    public async Task Release_disposes_watchers_for_the_path_and_everything_beneath_it()
    {
        var featureRoot = MakeDir("feature");
        var worktree = Directory.CreateDirectory(Path.Combine(featureRoot, "Repo")).FullName;
        var other = MakeDir("other");
        using var l1 = _manager.Acquire(featureRoot);
        using var l2 = _manager.Acquire(worktree);
        using var l3 = _manager.Acquire(other);
        Assert.Equal(3, _manager.ActiveWatcherCount);

        using var scope = await _releaser.ReleaseAsync([worktree, featureRoot], CancellationToken.None);

        Assert.Equal(1, _manager.ActiveWatcherCount);
        Assert.True(_manager.TryGetCoverage(other, out _));
        Assert.False(_manager.TryGetCoverage(worktree, out _));
        Assert.False(_manager.TryGetCoverage(featureRoot, out _));
    }

    [Fact]
    public async Task Released_path_gets_no_new_watcher_or_scan_until_the_scope_ends()
    {
        var worktree = MakeDir("wt");
        var scope = await _releaser.ReleaseAsync([worktree], CancellationToken.None);

        using (_manager.Acquire(worktree))
        {
            Assert.Equal(0, _manager.ActiveWatcherCount);
        }

        _coordinator.MarkDirty(worktree);
        var result = await _coordinator.RefreshNowAsync(worktree, CancellationToken.None);
        await Task.Delay(150);
        Assert.False(result.Success);
        Assert.Equal("PathReleased", result.ErrorCode);
        Assert.Equal(0, _fake.CallsFor(worktree));

        scope.Dispose();

        using (_manager.Acquire(worktree))
        {
            Assert.Equal(1, _manager.ActiveWatcherCount);
        }

        Assert.True((await _coordinator.RefreshNowAsync(worktree, CancellationToken.None)).Success);
    }

    [Fact]
    public async Task Overlapping_scopes_release_independently()
    {
        var worktree = MakeDir("wt2");
        var first = await _releaser.ReleaseAsync([worktree], CancellationToken.None);
        var second = await _releaser.ReleaseAsync([worktree], CancellationToken.None);

        first.Dispose();
        Assert.True(_gate.IsBlocked(worktree));
        second.Dispose();
        Assert.False(_gate.IsBlocked(worktree));
    }

    [Fact]
    public async Task Release_waits_for_a_running_scan_to_finish()
    {
        var worktree = MakeDir("wt3");
        _fake.Delay = TimeSpan.FromMilliseconds(400);
        var scan = _coordinator.RefreshNowAsync(worktree, CancellationToken.None);
        await Task.Delay(100);

        using var scope = await _releaser.ReleaseAsync([worktree], CancellationToken.None);

        Assert.True(scan.IsCompleted);
    }
}