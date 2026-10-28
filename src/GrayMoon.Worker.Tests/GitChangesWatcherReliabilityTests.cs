using GrayMoon.Common.Git;
using GrayMoon.Worker.Abstractions;
using GrayMoon.Worker.Services;
using GrayMoon.Worker.Services.GitChanges;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GrayMoon.Worker.Tests;

/// <summary>
/// Watcher lifecycle guarantees from docs/todo/GRAYMOON_WATCHER_RELIABILITY_*: deterministic expiry and renewal,
/// leases that cannot touch a newer entry, honest health, linked-worktree metadata watching and scan retry.
/// </summary>
[Trait("Category", "WatcherReliability")]
public sealed class GitChangesWatcherReliabilityTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("graymoon-watcher-reliability-").FullName;
    private readonly ManualTimeProvider _time = new();

    public void Dispose()
    {
        try
        {
            // Git marks object files read-only on Windows.
            foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(_root, true);
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    private string NewFolder(string name)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private (GitRepositoryWatcherManager Manager, GitStatusRefreshCoordinator Coordinator, FakeRepositoryGitChangesService Service, RepositoryAccess Access) CreateManager(
        GitChangesOptions? options = null)
    {
        options ??= new GitChangesOptions { WatcherDebounceMilliseconds = 50 };
        var service = new FakeRepositoryGitChangesService();
        var coordinator = new GitStatusRefreshCoordinator(
            service, new GitChangesSnapshotCache(), Options.Create(options), NullLogger<GitStatusRefreshCoordinator>.Instance);
        var access = new RepositoryAccess(NullLogger<RepositoryAccess>.Instance);
        var manager = new GitRepositoryWatcherManager(
            coordinator, new GitChangesSnapshotCache(), new GitChangesRepositoryRegistry(), Options.Create(options),
            NullLoggerFactory.Instance, NullLogger<GitRepositoryWatcherManager>.Instance, access, _time);
        return (manager, coordinator, service, access);
    }

    // ----- Expiry and renewal -----

    [Fact]
    public void Entry_expires_at_its_deadline_not_before()
    {
        var (manager, coordinator, _, _) = CreateManager();
        using var _m = manager;
        using var _c = coordinator;
        var folder = NewFolder("a");

        manager.Acquire(folder).Dispose();

        Assert.Equal(0, manager.SweepExpired(_time.GetUtcNow().AddMinutes(9)));
        Assert.Equal(1, manager.ActiveWatcherCount);

        Assert.Equal(1, manager.SweepExpired(_time.GetUtcNow().AddMinutes(10)));
        Assert.Equal(0, manager.ActiveWatcherCount);
        Assert.False(manager.TryGetCoverage(folder, out _));
    }

    [Fact]
    public void A_held_lease_never_expires()
    {
        var (manager, coordinator, _, _) = CreateManager();
        using var _m = manager;
        using var _c = coordinator;
        var folder = NewFolder("a");

        using var lease = manager.Acquire(folder);

        Assert.Equal(0, manager.SweepExpired(_time.GetUtcNow().AddHours(5)));
        Assert.Equal(1, manager.ActiveWatcherCount);
    }

    [Fact]
    public void Touch_extends_the_deadline_without_a_lease()
    {
        var (manager, coordinator, _, _) = CreateManager();
        using var _m = manager;
        using var _c = coordinator;
        var folder = NewFolder("a");
        manager.Acquire(folder).Dispose();

        _time.Advance(TimeSpan.FromMinutes(9));
        Assert.Equal(WatcherTouchResult.Renewed, manager.Touch(folder));

        // The original deadline (minute 10) has passed, the renewed one (minute 19) has not.
        _time.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(0, manager.SweepExpired(_time.GetUtcNow()));
        Assert.Equal(1, manager.ActiveWatcherCount);

        _time.Advance(TimeSpan.FromMinutes(9));
        Assert.Equal(1, manager.SweepExpired(_time.GetUtcNow()));
    }

    [Fact]
    public async Task Touch_creates_a_missing_watcher_and_queues_a_refresh()
    {
        var (manager, coordinator, service, _) = CreateManager();
        using var _m = manager;
        using var _c = coordinator;
        var folder = NewFolder("a");

        Assert.Equal(WatcherTouchResult.Created, manager.Touch(folder));
        Assert.Equal(1, manager.HealthyWatcherCount);

        for (var i = 0; i < 40 && service.CallCount == 0; i++)
            await Task.Delay(50);
        Assert.True(service.CallCount >= 1, "a created watcher must queue a refresh for changes made while nothing watched");

        // Renewing a healthy watcher queues nothing new.
        var before = service.CallCount;
        Assert.Equal(WatcherTouchResult.Renewed, manager.Touch(folder));
        await Task.Delay(300);
        Assert.Equal(before, service.CallCount);
    }

    [Fact]
    public async Task Touch_of_a_folder_being_removed_creates_nothing()
    {
        var (manager, coordinator, _, access) = CreateManager();
        using var _m = manager;
        using var _c = coordinator;
        var folder = NewFolder("a");

        var scope = await access.AcquireExclusiveAsync([folder], CancellationToken.None);
        try
        {
            Assert.Equal(WatcherTouchResult.UnderRemoval, manager.Touch(folder));
            Assert.Equal(0, manager.ActiveWatcherCount);
        }
        finally
        {
            await DisposeScopeAsync(scope);
        }
    }

    // ----- Leases -----

    [Fact]
    public async Task A_stale_lease_cannot_release_a_newer_entry()
    {
        var (manager, coordinator, _, access) = CreateManager();
        using var _m = manager;
        using var _c = coordinator;
        var folder = NewFolder("a");

        var staleLease = manager.Acquire(folder);

        // A removal claims the folder: the entry is released while a lease on it is still outstanding.
        var scope = await access.AcquireExclusiveAsync([folder], CancellationToken.None);
        Assert.Equal(0, manager.ActiveWatcherCount);
        await DisposeScopeAsync(scope);

        using var currentLease = manager.Acquire(folder);
        Assert.Equal(1, manager.ActiveWatcherCount);

        staleLease.Dispose();

        // The newer entry still has its lease: it must not become expirable.
        Assert.Equal(0, manager.SweepExpired(_time.GetUtcNow().AddHours(5)));
        Assert.Equal(1, manager.ActiveWatcherCount);
    }

    [Fact]
    public void Disposing_a_lease_twice_is_harmless()
    {
        var (manager, coordinator, _, _) = CreateManager();
        using var _m = manager;
        using var _c = coordinator;
        var folder = NewFolder("a");

        var first = manager.Acquire(folder);
        using var second = manager.Acquire(folder);
        first.Dispose();
        first.Dispose();

        // `second` is still held, so the entry cannot expire.
        Assert.Equal(0, manager.SweepExpired(_time.GetUtcNow().AddHours(5)));
    }

    [Fact]
    public void Disposing_the_manager_with_the_sweeper_pending_is_clean()
    {
        var (manager, coordinator, _, _) = CreateManager();
        using var _c = coordinator;
        var folder = NewFolder("a");
        manager.Acquire(folder).Dispose();

        manager.Dispose();
        manager.Dispose();

        Assert.Equal(0, manager.ActiveWatcherCount);
        Assert.Equal(0, manager.SweepExpired(_time.GetUtcNow().AddHours(5)));
        Assert.Equal(WatcherTouchResult.Degraded, manager.Touch(folder));
    }

    // ----- Health -----

    [Fact]
    public void An_unwatchable_folder_is_not_reported_as_healthy_and_recovers_once_it_exists()
    {
        var (manager, coordinator, _, _) = CreateManager();
        using var _m = manager;
        using var _c = coordinator;
        var folder = Path.Combine(_root, "later");

        using var lease = manager.Acquire(folder);
        Assert.Equal(1, manager.ActiveWatcherCount);
        Assert.Equal(0, manager.HealthyWatcherCount);

        // Within the cooldown nothing is retried.
        Directory.CreateDirectory(folder);
        Assert.Equal(WatcherTouchResult.Degraded, manager.Touch(folder));
        Assert.Equal(0, manager.HealthyWatcherCount);

        _time.Advance(TimeSpan.FromSeconds(31));
        Assert.Equal(WatcherTouchResult.Recovered, manager.Touch(folder));
        Assert.Equal(1, manager.HealthyWatcherCount);
        Assert.Equal(WatcherTouchResult.Renewed, manager.Touch(folder));
    }

    // ----- Linked worktrees -----

    [Fact]
    public void Resolve_metadata_directory_handles_directory_file_relative_and_broken_cases()
    {
        var normal = NewFolder("normal");
        Directory.CreateDirectory(Path.Combine(normal, ".git"));
        Assert.Equal(Path.Combine(normal, ".git"), GitRepositoryWatcher.ResolveGitMetadataDirectory(normal, out var reason));
        Assert.Null(reason);

        var gitdir = NewFolder("main-gitdir");
        var absolute = NewFolder("wt-absolute");
        File.WriteAllText(Path.Combine(absolute, ".git"), $"gitdir: {gitdir}\n");
        Assert.Equal(gitdir, GitRepositoryWatcher.ResolveGitMetadataDirectory(absolute, out reason));

        var relative = NewFolder("wt-relative");
        File.WriteAllText(Path.Combine(relative, ".git"), "gitdir: ../main-gitdir\n");
        Assert.Equal(gitdir, GitRepositoryWatcher.ResolveGitMetadataDirectory(relative, out _));

        var missing = NewFolder("wt-missing");
        File.WriteAllText(Path.Combine(missing, ".git"), "gitdir: ../nowhere\n");
        Assert.Null(GitRepositoryWatcher.ResolveGitMetadataDirectory(missing, out reason));
        Assert.Contains("missing directory", reason);

        var bare = NewFolder("no-git");
        Assert.Null(GitRepositoryWatcher.ResolveGitMetadataDirectory(bare, out reason));
        Assert.Equal(".git not found", reason);

        var garbage = NewFolder("garbage");
        File.WriteAllText(Path.Combine(garbage, ".git"), "not a gitdir file\n");
        Assert.Null(GitRepositoryWatcher.ResolveGitMetadataDirectory(garbage, out reason));
        Assert.Contains("no gitdir", reason);
    }

    [Fact]
    public async Task A_linked_worktree_sees_git_add_like_a_normal_checkout()
    {
        var main = GitConfigTestSupport.CreateRepo(_root, "main-repo");
        var worktree = Path.Combine(_root, "linked-worktree");
        GitConfigTestSupport.Git(main, $"worktree add -b feature \"{worktree}\"");

        // Modify before watching so the later `git add` touches only the index, never the working tree.
        File.WriteAllText(Path.Combine(main, "README.md"), "changed in main\n");
        File.WriteAllText(Path.Combine(worktree, "README.md"), "changed in worktree\n");

        var mainEvents = 0;
        var worktreeEvents = 0;
        using var mainWatcher = new GitRepositoryWatcher(main, NullLogger.Instance);
        using var worktreeWatcher = new GitRepositoryWatcher(worktree, NullLogger.Instance);
        mainWatcher.Changed += () => Interlocked.Increment(ref mainEvents);
        worktreeWatcher.Changed += () => Interlocked.Increment(ref worktreeEvents);

        Assert.True(worktreeWatcher.IsMetadataWatching);
        Assert.Null(worktreeWatcher.MetadataWatcherSkipReason);
        Assert.True(worktreeWatcher.IsHealthy);

        GitConfigTestSupport.Git(main, "add README.md");
        GitConfigTestSupport.Git(worktree, "add README.md");

        for (var i = 0; i < 60 && (Volatile.Read(ref mainEvents) == 0 || Volatile.Read(ref worktreeEvents) == 0); i++)
            await Task.Delay(100);

        Assert.True(Volatile.Read(ref mainEvents) > 0, "control: .git/index change must invalidate a normal checkout");
        Assert.True(Volatile.Read(ref worktreeEvents) > 0, "staging inside a linked worktree must invalidate its own status");
    }

    // ----- Scan retry -----

    [Fact]
    public async Task A_failed_watcher_scan_is_retried_with_backoff_then_stops()
    {
        var options = new GitChangesOptions { WatcherDebounceMilliseconds = 20, WatcherScanRetryDelaysSeconds = [1, 1] };
        var service = new ScriptedStatusService(failures: int.MaxValue);
        using var coordinator = new GitStatusRefreshCoordinator(
            service, new GitChangesSnapshotCache(), Options.Create(options), NullLogger<GitStatusRefreshCoordinator>.Instance);

        coordinator.MarkDirty(_root);

        // First attempt plus the two configured retries, then it gives up.
        for (var i = 0; i < 100 && service.Calls < 3; i++)
            await Task.Delay(100);
        await Task.Delay(2500);

        Assert.Equal(3, service.Calls);
    }

    [Fact]
    public async Task A_retry_that_succeeds_stops_retrying()
    {
        var options = new GitChangesOptions { WatcherDebounceMilliseconds = 20, WatcherScanRetryDelaysSeconds = [1, 1] };
        var service = new ScriptedStatusService(failures: 1);
        using var coordinator = new GitStatusRefreshCoordinator(
            service, new GitChangesSnapshotCache(), Options.Create(options), NullLogger<GitStatusRefreshCoordinator>.Instance);

        coordinator.MarkDirty(_root);

        for (var i = 0; i < 60 && service.Calls < 2; i++)
            await Task.Delay(100);
        await Task.Delay(2500);

        Assert.Equal(2, service.Calls);
    }

    [Fact]
    public async Task A_path_under_removal_is_not_retried()
    {
        var options = new GitChangesOptions { WatcherDebounceMilliseconds = 20, WatcherScanRetryDelaysSeconds = [1] };
        var service = new ScriptedStatusService(failures: int.MaxValue, errorCode: "PathUnderRemoval");
        using var coordinator = new GitStatusRefreshCoordinator(
            service, new GitChangesSnapshotCache(), Options.Create(options), NullLogger<GitStatusRefreshCoordinator>.Instance);

        coordinator.MarkDirty(_root);

        for (var i = 0; i < 40 && service.Calls < 1; i++)
            await Task.Delay(50);
        await Task.Delay(2000);

        Assert.Equal(1, service.Calls);
    }

    private static async Task DisposeScopeAsync(object scope)
    {
        switch (scope)
        {
            case IAsyncDisposable asyncDisposable:
                await asyncDisposable.DisposeAsync();
                break;
            case IDisposable disposable:
                disposable.Dispose();
                break;
        }
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 28, 9, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;

        // The sweeper is driven explicitly through SweepExpired.
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) => new NoopTimer();

        private sealed class NoopTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;

            public void Dispose()
            {
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class ScriptedStatusService(int failures, string errorCode = "Simulated") : IRepositoryGitChangesService
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public Task<GitChangeStatusResult> GetStatusAsync(string repoPath, long snapshotVersion, CancellationToken cancellationToken, bool includeLineStats = false)
        {
            var call = Interlocked.Increment(ref _calls);
            return Task.FromResult(call <= failures
                ? new GitChangeStatusResult { Success = false, ErrorCode = errorCode, ErrorMessage = "Simulated failure." }
                : new GitChangeStatusResult { Success = true });
        }

        public Task<GitDiffDocument> GetDiffAsync(string repoPath, GitDiffRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<GitMutationResult> StageAsync(string repoPath, GitStageOperationRequest request, long nextSnapshotVersion, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<GitMutationResult> UnstageAsync(string repoPath, GitStageOperationRequest request, long nextSnapshotVersion, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<GitMutationResult> DiscardAsync(string repoPath, GitStageOperationRequest request, long nextSnapshotVersion, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<GitCommitResult> CommitAsync(string repoPath, GitCommitOperationRequest request, long nextSnapshotVersion, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
