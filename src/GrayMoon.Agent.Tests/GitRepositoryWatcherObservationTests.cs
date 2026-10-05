using GrayMoon.Agent.Services.GitChanges;
using GrayMoon.Common.Git;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GrayMoon.Agent.Tests;

public sealed class GitRepositoryWatcherObservationTests
{
    [Fact]
    public void Created_event_records_utc_observation_timestamp_and_path()
    {
        var before = DateTimeOffset.UtcNow;
        var observation = GitRepositoryWatcher.TryCreateWorkTreeObservation(
            @"C:\repo\src\File.cs",
            GitRepositoryObservedChangeKind.Created,
            DateTimeOffset.UtcNow,
            oldPath: null);

        Assert.NotNull(observation);
        Assert.Equal(GitRepositoryObservedChangeKind.Created, observation!.Kind);
        Assert.Equal(@"C:\repo\src\File.cs", observation.Path);
        Assert.Null(observation.OldPath);
        Assert.True(observation.ObservedAt >= before);
        Assert.Equal(TimeSpan.Zero, observation.ObservedAt.Offset);
    }

    [Fact]
    public void Changed_event_records_timestamp_and_path()
    {
        var at = DateTimeOffset.Parse("2026-03-15T12:00:00Z");
        var observation = GitRepositoryWatcher.TryCreateWorkTreeObservation(
            @"C:\repo\readme.md",
            GitRepositoryObservedChangeKind.Changed,
            at,
            oldPath: null);

        Assert.NotNull(observation);
        Assert.Equal(GitRepositoryObservedChangeKind.Changed, observation!.Kind);
        Assert.Equal(@"C:\repo\readme.md", observation.Path);
        Assert.Equal(at, observation.ObservedAt);
    }

    [Fact]
    public void Deleted_event_records_timestamp_and_path()
    {
        var observation = GitRepositoryWatcher.TryCreateWorkTreeObservation(
            @"C:\repo\gone.txt",
            GitRepositoryObservedChangeKind.Deleted,
            DateTimeOffset.UtcNow,
            oldPath: null);

        Assert.NotNull(observation);
        Assert.Equal(GitRepositoryObservedChangeKind.Deleted, observation!.Kind);
        Assert.Equal(@"C:\repo\gone.txt", observation.Path);
    }

    [Fact]
    public void Rename_records_new_path_and_old_path()
    {
        var observation = GitRepositoryWatcher.TryCreateWorkTreeObservation(
            @"C:\repo\new-name.cs",
            GitRepositoryObservedChangeKind.Renamed,
            DateTimeOffset.UtcNow,
            oldPath: @"C:\repo\old-name.cs");

        Assert.NotNull(observation);
        Assert.Equal(GitRepositoryObservedChangeKind.Renamed, observation!.Kind);
        Assert.Equal(@"C:\repo\new-name.cs", observation.Path);
        Assert.Equal(@"C:\repo\old-name.cs", observation.OldPath);
    }

    [Fact]
    public void Git_metadata_paths_are_not_emitted_as_work_tree_observations()
    {
        var separator = Path.DirectorySeparatorChar;
        var underGit = $@"C:\repo{separator}.git{separator}index";
        var gitDirItself = $@"C:\repo{separator}.git";

        Assert.Null(GitRepositoryWatcher.TryCreateWorkTreeObservation(
            underGit, GitRepositoryObservedChangeKind.Changed, DateTimeOffset.UtcNow, null));
        Assert.Null(GitRepositoryWatcher.TryCreateWorkTreeObservation(
            gitDirItself, GitRepositoryObservedChangeKind.Changed, DateTimeOffset.UtcNow, null));
    }

    [Fact]
    public void Coverage_records_discontinuity_separately_from_observations()
    {
        var coverage = new GitRepositoryWatcherCoverage { StartedAt = DateTimeOffset.UtcNow };
        var at = DateTimeOffset.UtcNow;

        coverage.RecordDiscontinuity(at);

        Assert.Equal(at, coverage.LastDiscontinuityAt);
        Assert.Null(coverage.LastObservedAt);
    }

    [Fact]
    public void Observation_buffer_is_bounded_and_drops_oldest()
    {
        var buffer = new GitRepositoryWatcherObservationBuffer(capacity: 3);

        for (var i = 0; i < 5; i++)
        {
            buffer.Add(new GitRepositoryObservedChange
            {
                ObservedAt = DateTimeOffset.UtcNow,
                Path = $"file{i}.txt",
                Kind = GitRepositoryObservedChangeKind.Changed,
            });
        }

        Assert.Equal(3, buffer.Count);
        var snapshot = buffer.Snapshot();
        Assert.Equal(["file2.txt", "file3.txt", "file4.txt"], snapshot.Select(o => o.Path).ToArray());
    }

    [Fact]
    public void Manager_records_coverage_start_and_observations_without_replacing_invalidation()
    {
        var tempDir = Directory.CreateTempSubdirectory("graymoon-obs-test-").FullName;
        try
        {
            var fake = new FakeRepositoryGitChangesService { Delay = TimeSpan.Zero };
            var options = new GitChangesOptions { WatcherDebounceMilliseconds = 50 };
            using var coordinator = new GitStatusRefreshCoordinator(
                fake, new GitChangesSnapshotCache(), Options.Create(options), NullLogger<GitStatusRefreshCoordinator>.Instance);
            using var manager = new GitRepositoryWatcherManager(
                coordinator, new GitChangesSnapshotCache(), new GitChangesRepositoryRegistry(),
                Options.Create(options), NullLoggerFactory.Instance, NullLogger<GitRepositoryWatcherManager>.Instance);

            using var lease = manager.Acquire(tempDir);

            Assert.True(manager.TryGetCoverage(tempDir, out var coverage));
            Assert.NotNull(coverage);
            Assert.Null(coverage!.EndedAt);
            Assert.True(coverage.StartedAt <= DateTimeOffset.UtcNow);

            var observed = GitRepositoryWatcher.TryCreateWorkTreeObservation(
                Path.Combine(tempDir, "a.txt"),
                GitRepositoryObservedChangeKind.Created,
                DateTimeOffset.UtcNow,
                null)!;
            coverage.RecordObservation(observed.ObservedAt);

            Assert.NotNull(coverage.LastObservedAt);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public void Manager_marks_coverage_ended_on_dispose()
    {
        var tempDir = Directory.CreateTempSubdirectory("graymoon-obs-end-").FullName;
        try
        {
            var fake = new FakeRepositoryGitChangesService();
            var options = new GitChangesOptions();
            using var coordinator = new GitStatusRefreshCoordinator(
                fake, new GitChangesSnapshotCache(), Options.Create(options), NullLogger<GitStatusRefreshCoordinator>.Instance);
            var manager = new GitRepositoryWatcherManager(
                coordinator, new GitChangesSnapshotCache(), new GitChangesRepositoryRegistry(),
                Options.Create(options), NullLoggerFactory.Instance, NullLogger<GitRepositoryWatcherManager>.Instance);

            using var lease = manager.Acquire(tempDir);
            Assert.True(manager.TryGetCoverage(tempDir, out var coverage));
            Assert.NotNull(coverage);

            manager.Dispose();

            Assert.NotNull(coverage!.EndedAt);
            Assert.Equal(0, manager.ActiveWatcherCount);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public async Task Work_tree_file_change_records_observation_and_still_triggers_scan()
    {
        var tempDir = Directory.CreateTempSubdirectory("graymoon-obs-fs-").FullName;
        try
        {
            var fake = new FakeRepositoryGitChangesService { Delay = TimeSpan.Zero };
            var options = new GitChangesOptions { WatcherDebounceMilliseconds = 50 };
            using var coordinator = new GitStatusRefreshCoordinator(
                fake, new GitChangesSnapshotCache(), Options.Create(options), NullLogger<GitStatusRefreshCoordinator>.Instance);
            using var manager = new GitRepositoryWatcherManager(
                coordinator, new GitChangesSnapshotCache(), new GitChangesRepositoryRegistry(),
                Options.Create(options), NullLoggerFactory.Instance, NullLogger<GitRepositoryWatcherManager>.Instance);

            using var lease = manager.Acquire(tempDir);

            await File.WriteAllTextAsync(Path.Combine(tempDir, "watched.txt"), "hello");

            var sawScan = await WaitForAsync(() => fake.CallCount > 0, TimeSpan.FromSeconds(5));
            Assert.True(sawScan, "Expected invalidation scan to still run.");

            var sawObservation = await WaitForAsync(
                () => manager.TryGetRecentObservations(tempDir, out var obs) && obs.Count > 0,
                TimeSpan.FromSeconds(5));
            Assert.True(sawObservation, "Expected at least one working-tree observation.");
            Assert.True(manager.TryGetCoverage(tempDir, out var coverage));
            Assert.NotNull(coverage!.LastObservedAt);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort */ }
        }
    }

    private static async Task<bool> WaitForAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
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