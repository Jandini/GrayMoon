using GrayMoon.Worker.Services.GitChanges;
using Microsoft.Extensions.Logging.Abstractions;

namespace GrayMoon.Worker.Tests;

public sealed class GitRepositoryWatcherNestedRepoTests
{
    [Fact]
    public void Event_under_nested_repo_is_ignored()
    {
        var root = Path.Combine(Path.GetTempPath(), "graymoon-nested-root");
        var nested = Path.Combine(root, "Avr.Api");
        string[] nestedRoots = [nested];

        Assert.Null(GitRepositoryWatcher.TryCreateWorkTreeObservation(
            Path.Combine(nested, "src", "File.cs"), GitRepositoryObservedChangeKind.Changed, DateTimeOffset.UtcNow, null, nestedRoots));
        Assert.Null(GitRepositoryWatcher.TryCreateWorkTreeObservation(
            nested, GitRepositoryObservedChangeKind.Deleted, DateTimeOffset.UtcNow, null, nestedRoots));
        Assert.Null(GitRepositoryWatcher.TryCreateWorkTreeObservation(
            Path.Combine(nested.ToUpperInvariant(), "File.cs"), GitRepositoryObservedChangeKind.Created, DateTimeOffset.UtcNow, null, nestedRoots));

        // A sibling whose name merely starts with the nested folder's name is not under it.
        Assert.NotNull(GitRepositoryWatcher.TryCreateWorkTreeObservation(
            Path.Combine(root, "Avr.Api.Docs", "readme.md"), GitRepositoryObservedChangeKind.Changed, DateTimeOffset.UtcNow, null, nestedRoots));
    }

    [Fact]
    public void Event_at_root_file_is_observed()
    {
        var root = Path.Combine(Path.GetTempPath(), "graymoon-nested-root");
        string[] nestedRoots = [Path.Combine(root, "Avr.Api")];

        var observation = GitRepositoryWatcher.TryCreateWorkTreeObservation(
            Path.Combine(root, ".graymoon.json"), GitRepositoryObservedChangeKind.Changed, DateTimeOffset.UtcNow, null, nestedRoots);

        Assert.NotNull(observation);
        Assert.Equal(Path.Combine(root, ".graymoon.json"), observation!.Path);
    }

    [Fact]
    public void Newly_created_nested_repo_is_excluded_after_refresh()
    {
        var root = Directory.CreateTempSubdirectory("graymoon-nested-watch-").FullName;
        try
        {
            using var watcher = new GitRepositoryWatcher(root, NullLogger.Instance);
            Assert.Empty(watcher.NestedRepoRoots);

            var nested = Path.Combine(root, "Avr.Api");
            Directory.CreateDirectory(Path.Combine(nested, ".git"));
            var plainFolder = Path.Combine(root, "docs");
            Directory.CreateDirectory(plainFolder);

            var filePath = Path.Combine(nested, "File.cs");
            // The watcher's own events may already have refreshed the set; an explicit refresh makes it deterministic.
            watcher.RefreshNestedRepoRoots();

            var roots = Assert.Single(watcher.NestedRepoRoots);
            Assert.Equal(Path.GetFullPath(nested), roots, ignoreCase: true);
            Assert.Null(GitRepositoryWatcher.TryCreateWorkTreeObservation(
                filePath, GitRepositoryObservedChangeKind.Changed, DateTimeOffset.UtcNow, null, watcher.NestedRepoRoots));
            Assert.NotNull(GitRepositoryWatcher.TryCreateWorkTreeObservation(
                Path.Combine(plainFolder, "a.md"), GitRepositoryObservedChangeKind.Changed, DateTimeOffset.UtcNow, null, watcher.NestedRepoRoots));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { /* best-effort */ }
        }
    }
}
