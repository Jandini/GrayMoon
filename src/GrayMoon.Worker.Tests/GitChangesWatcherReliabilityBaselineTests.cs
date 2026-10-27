using GrayMoon.Common.Git;
using GrayMoon.Worker.Abstractions;
using GrayMoon.Worker.Services.GitChanges;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GrayMoon.Worker.Tests;

/// <summary>
/// Phase 0 baseline for docs/todo/GRAYMOON_WATCHER_RELIABILITY_*. These tests PIN TODAY'S behavior of three
/// hazards so Phase 1 changes them on purpose: each assertion marked "Phase 1 flips this" is expected to be
/// inverted (or replaced) when the hazard is fixed. They are not a statement that the behavior is desirable.
/// </summary>
[Trait("Category", "WatcherReliabilityBaseline")]
public sealed class GitChangesWatcherReliabilityBaselineTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("graymoon-watcher-baseline-").FullName;

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

    [Fact]
    public void F7_unwatchable_folder_still_yields_a_live_manager_entry_with_no_os_watcher()
    {
        var missing = Path.Combine(_root, "does-not-exist");
        var options = new GitChangesOptions();
        using var coordinator = new GitStatusRefreshCoordinator(
            new FailingStatusService(), new GitChangesSnapshotCache(), Options.Create(options), NullLogger<GitStatusRefreshCoordinator>.Instance);
        using var manager = new GitRepositoryWatcherManager(
            coordinator, new GitChangesSnapshotCache(), new GitChangesRepositoryRegistry(), Options.Create(options),
            NullLoggerFactory.Instance, NullLogger<GitRepositoryWatcherManager>.Instance,
            new GrayMoon.Worker.Services.RepositoryAccess(NullLogger<GrayMoon.Worker.Services.RepositoryAccess>.Instance));

        using var watcher = new GitRepositoryWatcher(missing, NullLogger.Instance);
        using var lease = manager.Acquire(missing);

        Assert.False(watcher.IsWorkTreeWatching);

        // Phase 1 flips this: an entry whose OS watcher failed to start must not count as a healthy, renewable watcher.
        Assert.Equal(1, manager.ActiveWatcherCount);
    }

    [Fact]
    public void F6_linked_worktree_has_no_metadata_watcher_but_a_normal_checkout_has_one()
    {
        var main = GitConfigTestSupport.CreateRepo(_root, "main-repo");
        var worktree = Path.Combine(_root, "linked-worktree");
        GitConfigTestSupport.Git(main, $"worktree add -b feature \"{worktree}\"");

        using var mainWatcher = new GitRepositoryWatcher(main, NullLogger.Instance);
        using var worktreeWatcher = new GitRepositoryWatcher(worktree, NullLogger.Instance);

        Assert.True(mainWatcher.IsWorkTreeWatching);
        Assert.True(mainWatcher.IsMetadataWatching);
        Assert.Null(mainWatcher.MetadataWatcherSkipReason);

        Assert.True(worktreeWatcher.IsWorkTreeWatching);

        // Phase 1 flips this: the worktree's index/HEAD live under <main>/.git/worktrees/<name>/ and must be watched.
        Assert.False(worktreeWatcher.IsMetadataWatching);
        Assert.Contains("linked worktree", worktreeWatcher.MetadataWatcherSkipReason);
    }

    [Fact]
    public async Task F6_git_add_in_a_linked_worktree_raises_no_watcher_event_but_in_a_normal_checkout_it_does()
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

        GitConfigTestSupport.Git(main, "add README.md");
        GitConfigTestSupport.Git(worktree, "add README.md");

        for (var i = 0; i < 40 && Volatile.Read(ref mainEvents) == 0; i++)
            await Task.Delay(100);
        await Task.Delay(1000);

        Assert.True(Volatile.Read(ref mainEvents) > 0, "positive control: .git/index change must invalidate a normal checkout");

        // Phase 1 flips this: staging inside a Feature worktree must invalidate its own status.
        Assert.Equal(0, Volatile.Read(ref worktreeEvents));
    }

    [Fact]
    public async Task F8_failed_watcher_driven_scan_is_not_retried()
    {
        var service = new FailingStatusService();
        var options = new GitChangesOptions { WatcherDebounceMilliseconds = 50 };
        using var coordinator = new GitStatusRefreshCoordinator(
            service, new GitChangesSnapshotCache(), Options.Create(options), NullLogger<GitStatusRefreshCoordinator>.Instance);

        coordinator.MarkDirty(_root);
        for (var i = 0; i < 40 && service.Calls == 0; i++)
            await Task.Delay(50);
        await Task.Delay(1500);

        // Phase 1 flips this: a transient failure is retried with backoff instead of leaving the view stale.
        Assert.Equal(1, service.Calls);
    }

    private sealed class FailingStatusService : IRepositoryGitChangesService
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public Task<GitChangeStatusResult> GetStatusAsync(string repoPath, long snapshotVersion, CancellationToken cancellationToken, bool includeLineStats = false)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(new GitChangeStatusResult { Success = false, ErrorCode = "Simulated", ErrorMessage = "Simulated transient failure." });
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
