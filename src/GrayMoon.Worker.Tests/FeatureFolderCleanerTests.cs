using GrayMoon.Worker.Commands;
using GrayMoon.Worker.Jobs.Requests;
using GrayMoon.Worker.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace GrayMoon.Worker.Tests;

/// <summary>
/// <see cref="FeatureFolderCleaner"/>, <see cref="PendingDeleteMarker"/> and the two commands built on them: a removed
/// Feature's leftover folder is deleted, or marked pending deletion while a file is still in use, and the sweep later
/// deletes only folders that are marked, match their marker and do not belong to a live Feature.
/// </summary>
public sealed class FeatureFolderCleanerTests : IDisposable
{
    private readonly string _temp = Directory.CreateTempSubdirectory("graymoon-ffc-").FullName;
    private readonly string _storageRoot;
    private readonly FeatureFolderCleaner _cleaner = new(NullLogger<FeatureFolderCleaner>.Instance, new GrayMoon.Worker.Services.RepositoryAccess(NullLogger<GrayMoon.Worker.Services.RepositoryAccess>.Instance));

    public FeatureFolderCleanerTests()
    {
        _storageRoot = Path.Combine(_temp, "Shop", "features");
        Directory.CreateDirectory(_storageRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_temp, true); } catch { /* best-effort */ }
    }

    [Fact]
    public async Task Removes_a_leftover_folder_with_no_open_files()
    {
        var root = CreateFeature("login-fix", ("Api/src/a.cs", "x"), ("Web/readme.md", "y"));

        var result = await _cleaner.CleanupAsync(_storageRoot, root, "Shop", "login-fix", retry: false, requireMarker: false, CancellationToken.None);

        Assert.Equal(FeatureFolderCleanupOutcome.Removed, result.Outcome);
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public async Task Missing_folder_counts_as_removed()
    {
        var result = await _cleaner.CleanupAsync(_storageRoot, Path.Combine(_storageRoot, "gone"), null, null, retry: false, requireMarker: false, CancellationToken.None);

        Assert.Equal(FeatureFolderCleanupOutcome.Removed, result.Outcome);
    }

    [Fact]
    public async Task Open_file_leaves_the_folder_marked_and_a_later_pass_removes_it()
    {
        if (!OperatingSystem.IsWindows())
            return; // Open files only block deletes on Windows.

        var root = CreateFeature("login-fix", ("Api/locked.txt", "x"), ("Api/free.txt", "y"));
        var lockedPath = Path.Combine(root, "Api", "locked.txt");

        using (new FileStream(lockedPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var first = await _cleaner.CleanupAsync(_storageRoot, root, "Shop", "login-fix", retry: false, requireMarker: false, CancellationToken.None);

            Assert.Equal(FeatureFolderCleanupOutcome.PendingDeletion, first.Outcome);
            Assert.Equal(1, first.RemainingFileCount);
            Assert.False(File.Exists(Path.Combine(root, "Api", "free.txt")));

            var marker = PendingDeleteMarker.TryRead(root);
            Assert.NotNull(marker);
            Assert.Equal(root, marker.Folder, ignoreCase: true);
            Assert.Equal("Shop", marker.Workspace);
            Assert.Equal("login-fix", marker.Feature);
            Assert.True(File.Exists(Path.Combine(root, "Api", PendingDeleteMarker.FileName)));
        }

        var second = await _cleaner.CleanupAsync(_storageRoot, root, null, null, retry: false, requireMarker: false, CancellationToken.None);

        Assert.Equal(FeatureFolderCleanupOutcome.Removed, second.Outcome);
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public async Task Rewritten_marker_keeps_the_original_names_and_time()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var root = CreateFeature("login-fix", ("Api/locked.txt", "x"));
        var markedUtc = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        await File.WriteAllTextAsync(
            Path.Combine(root, PendingDeleteMarker.FileName),
            new PendingDeleteMarker(root, "Shop", "login-fix", markedUtc).Render());

        using (new FileStream(Path.Combine(root, "Api", "locked.txt"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var result = await _cleaner.CleanupAsync(_storageRoot, root, null, null, retry: false, requireMarker: false, CancellationToken.None);
            Assert.Equal(FeatureFolderCleanupOutcome.PendingDeletion, result.Outcome);
        }

        var marker = PendingDeleteMarker.TryRead(root);
        Assert.NotNull(marker);
        Assert.Equal("Shop", marker.Workspace);
        Assert.Equal("login-fix", marker.Feature);
        Assert.Equal(markedUtc, marker.MarkedUtc);
    }

    [Fact]
    public async Task Refuses_a_folder_that_holds_a_git_repository()
    {
        var root = CreateFeature("cloned", ("Api/readme.md", "x"));
        Directory.CreateDirectory(Path.Combine(root, "Api", ".git"));

        var result = await _cleaner.CleanupAsync(_storageRoot, root, null, null, retry: false, requireMarker: false, CancellationToken.None);

        Assert.Equal(FeatureFolderCleanupOutcome.Refused, result.Outcome);
        Assert.True(File.Exists(Path.Combine(root, "Api", "readme.md")));
        Assert.Null(PendingDeleteMarker.TryRead(root));
    }

    [Fact]
    public async Task Refuses_a_worktree_that_is_still_registered()
    {
        var root = CreateFeature("live", ("Api/readme.md", "x"));
        var adminDir = Path.Combine(_temp, "main", ".git", "worktrees", "Api");
        Directory.CreateDirectory(adminDir);
        await File.WriteAllTextAsync(Path.Combine(root, "Api", ".git"), $"gitdir: {adminDir}\n");

        var result = await _cleaner.CleanupAsync(_storageRoot, root, null, null, retry: false, requireMarker: false, CancellationToken.None);

        Assert.Equal(FeatureFolderCleanupOutcome.Refused, result.Outcome);
        Assert.True(File.Exists(Path.Combine(root, "Api", "readme.md")));
    }

    [Fact]
    public async Task Deletes_a_worktree_whose_registration_is_gone()
    {
        var root = CreateFeature("stale", ("Api/readme.md", "x"));
        var adminDir = Path.Combine(_temp, "main", ".git", "worktrees", "gone");
        await File.WriteAllTextAsync(Path.Combine(root, "Api", ".git"), $"gitdir: {adminDir}\n");

        var result = await _cleaner.CleanupAsync(_storageRoot, root, null, null, retry: false, requireMarker: false, CancellationToken.None);

        Assert.Equal(FeatureFolderCleanupOutcome.Removed, result.Outcome);
        Assert.False(Directory.Exists(root));
    }

    [Theory]
    [InlineData(null, "C:\\x\\features\\a")]
    [InlineData("relative\\features", "relative\\features\\a")]
    public void ValidatePaths_requires_absolute_paths(string? storageRoot, string featureRoot)
    {
        Assert.NotNull(FeatureFolderCleaner.ValidatePaths(storageRoot, featureRoot));
    }

    [Fact]
    public void ValidatePaths_requires_a_folder_below_a_features_folder()
    {
        var storage = Path.Combine(_temp, "Shop", "features");

        Assert.Null(FeatureFolderCleaner.ValidatePaths(storage, Path.Combine(storage, "a")));
        Assert.Null(FeatureFolderCleaner.ValidatePaths(storage, Path.Combine(storage, "team", "login")));
        Assert.NotNull(FeatureFolderCleaner.ValidatePaths(storage, storage));
        Assert.NotNull(FeatureFolderCleaner.ValidatePaths(storage, Path.Combine(storage, "..", "x")));
        Assert.NotNull(FeatureFolderCleaner.ValidatePaths(storage, Path.Combine(_temp, "elsewhere")));
        var notFeatures = Path.Combine(_temp, "Shop");
        Assert.NotNull(FeatureFolderCleaner.ValidatePaths(notFeatures, Path.Combine(notFeatures, "a")));
    }

    [Fact]
    public void Marker_round_trips_and_ignores_text_without_a_block()
    {
        var marker = new PendingDeleteMarker(@"C:\x\features\a", "Shop", "a-->b", new DateTime(2026, 10, 9, 10, 12, 31, DateTimeKind.Utc));

        var parsed = PendingDeleteMarker.Parse(marker.Render());

        Assert.NotNull(parsed);
        Assert.Equal(marker.Folder, parsed.Folder);
        Assert.Equal("Shop", parsed.Workspace);
        Assert.Equal(marker.MarkedUtc, parsed.MarkedUtc);
        Assert.Null(PendingDeleteMarker.Parse("# This folder is pending deletion\n"));
    }

    [Fact]
    public async Task Sweep_removes_only_marked_matching_folders_that_are_not_excluded()
    {
        var marked = CreateFeature("marked", ("Api/a.txt", "x"));
        WriteMarker(marked, marked);
        var unmarked = CreateFeature("unmarked", ("Api/a.txt", "x"));
        var live = CreateFeature("live", ("Api/a.txt", "x"));
        WriteMarker(live, live);
        var mismatched = CreateFeature("mismatched", ("Api/a.txt", "x"));
        WriteMarker(mismatched, Path.Combine(_storageRoot, "other"));

        var command = new SweepPendingFeatureFoldersCommand(_cleaner);
        var response = await command.ExecuteAsync(new SweepPendingFeatureFoldersRequest
        {
            FeatureStorageRoot = _storageRoot,
            WorkspaceName = "Shop",
            ExcludeFeatureNames = ["LIVE"],
        });

        Assert.Equal(1, response.Removed);
        Assert.Equal(0, response.StillPending);
        Assert.Equal(0, response.Refused);
        Assert.False(Directory.Exists(marked));
        Assert.True(Directory.Exists(unmarked));
        Assert.True(Directory.Exists(live));
        Assert.True(Directory.Exists(mismatched));
    }

    [Fact]
    public async Task Sweep_finds_a_nested_feature_folder_and_removes_its_empty_parent()
    {
        var marked = CreateFeature("team/login", ("Api/a.txt", "x"));
        WriteMarker(marked, marked);
        var live = CreateFeature("team/live", ("Api/a.txt", "x"));
        WriteMarker(live, live);
        var lonely = CreateFeature("solo/old", ("Api/a.txt", "x"));
        WriteMarker(lonely, lonely);

        var command = new SweepPendingFeatureFoldersCommand(_cleaner);
        var response = await command.ExecuteAsync(new SweepPendingFeatureFoldersRequest
        {
            FeatureStorageRoot = _storageRoot,
            ExcludeFeatureNames = ["team/live"],
        });

        Assert.Equal(2, response.Removed);
        Assert.False(Directory.Exists(marked));
        Assert.True(Directory.Exists(live));
        Assert.True(Directory.Exists(Path.Combine(_storageRoot, "team")));
        Assert.False(Directory.Exists(Path.Combine(_storageRoot, "solo")));
        Assert.True(Directory.Exists(_storageRoot));
    }

    [Fact]
    public async Task Sweep_never_looks_inside_a_git_checkout()
    {
        var checkout = CreateFeature("live", ("Api/a.txt", "x"));
        await File.WriteAllTextAsync(Path.Combine(checkout, ".git"), "gitdir: somewhere\n");
        var inner = Path.Combine(checkout, "Api");
        WriteMarker(inner, inner);

        var command = new SweepPendingFeatureFoldersCommand(_cleaner);
        var response = await command.ExecuteAsync(new SweepPendingFeatureFoldersRequest { FeatureStorageRoot = _storageRoot });

        Assert.Equal(0, response.Removed + response.StillPending + response.Refused);
        Assert.True(File.Exists(Path.Combine(inner, "a.txt")));
    }

    [Fact]
    public async Task Sweep_of_a_missing_storage_root_does_nothing()
    {
        var command = new SweepPendingFeatureFoldersCommand(_cleaner);

        var response = await command.ExecuteAsync(new SweepPendingFeatureFoldersRequest
        {
            FeatureStorageRoot = Path.Combine(_temp, "Nope", "features"),
        });

        Assert.Equal(0, response.Removed + response.StillPending + response.Refused);
    }

    [Fact]
    public async Task Cleanup_command_reports_the_outcome_name()
    {
        var root = CreateFeature("done", ("Api/a.txt", "x"));
        var command = new CleanupFeatureFolderCommand(_cleaner);

        var response = await command.ExecuteAsync(new CleanupFeatureFolderRequest
        {
            FeatureStorageRoot = _storageRoot,
            FeatureRootPath = root,
            Retry = true,
        });

        Assert.Equal(nameof(FeatureFolderCleanupOutcome.Removed), response.Outcome);
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public async Task Only_if_marked_leaves_an_unmarked_folder_alone_and_cleans_a_marked_one()
    {
        var unmarked = CreateFeature("mine", ("notes.txt", "x"));
        var marked = CreateFeature("old", ("Api/a.txt", "x"));
        WriteMarker(marked, marked);
        var command = new CleanupFeatureFolderCommand(_cleaner);

        var untouched = await command.ExecuteAsync(new CleanupFeatureFolderRequest
        {
            FeatureStorageRoot = _storageRoot,
            FeatureRootPath = unmarked,
            OnlyIfMarked = true,
        });
        var cleaned = await command.ExecuteAsync(new CleanupFeatureFolderRequest
        {
            FeatureStorageRoot = _storageRoot,
            FeatureRootPath = marked,
            OnlyIfMarked = true,
        });

        Assert.Equal(nameof(FeatureFolderCleanupOutcome.NotMarked), untouched.Outcome);
        Assert.True(File.Exists(Path.Combine(unmarked, "notes.txt")));
        Assert.Equal(nameof(FeatureFolderCleanupOutcome.Removed), cleaned.Outcome);
        Assert.False(Directory.Exists(marked));
    }

    private string CreateFeature(string name, params (string RelativePath, string Content)[] files)
    {
        var root = Path.Combine(_storageRoot, name.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(root);
        foreach (var (relativePath, content) in files)
        {
            var path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }

        return root;
    }

    private static void WriteMarker(string folder, string markerFolder) =>
        File.WriteAllText(
            Path.Combine(folder, PendingDeleteMarker.FileName),
            new PendingDeleteMarker(markerFolder, "Shop", Path.GetFileName(folder), DateTime.UtcNow).Render());
}
