using GrayMoon.App.Services.GitChanges;
using GrayMoon.Common.Git;

namespace GrayMoon.App.Tests;

public class GitChangesEntryPageTests
{
    private static WorkspaceGitChangeEntryView Entry(
        string path,
        GitChangeKind index = GitChangeKind.None,
        GitChangeKind worktree = GitChangeKind.Modified) => new()
    {
        Path = path,
        IndexChange = index,
        WorktreeChange = worktree,
    };

    [Fact]
    public void GroupDirectChildren_repo_root_folders_first_then_files()
    {
        var entries = new[]
        {
            Entry("z.txt"),
            Entry("src/a.cs"),
            Entry("src/b.cs"),
            Entry("lib/x.cs"),
            Entry("a.txt"),
            Entry("nested/", worktree: GitChangeKind.Untracked),
        };

        var children = WorkspaceGitChangesReadService.GroupDirectChildren(string.Empty, entries);
        var ordered = WorkspaceGitChangesReadService.OrderChildren(children).ToList();

        Assert.Equal(["lib", "nested", "src", "a.txt", "z.txt"], ordered.Select(c => c.Name).ToList());
        Assert.True(ordered[0].IsFolder);
        Assert.True(ordered[1].IsDirectoryEntry);
        Assert.False(ordered[^1].IsFolder);
    }

    [Fact]
    public void GroupDirectChildren_under_prefix_only_direct()
    {
        var entries = new[]
        {
            Entry("src/Foo/a.cs"),
            Entry("src/Foo/bar/b.cs"),
            Entry("src/Foo/z.cs"),
            Entry("src/Other/x.cs"),
            Entry("root.cs"),
        };

        var children = WorkspaceGitChangesReadService.GroupDirectChildren("src/Foo", entries);
        var ordered = WorkspaceGitChangesReadService.OrderChildren(children).ToList();

        Assert.Equal(["bar", "a.cs", "z.cs"], ordered.Select(c => c.Name).ToList());
        Assert.Equal("src/Foo/bar", ordered[0].Path);
    }

    [Fact]
    public void GroupDirectChildren_matches_tree_builder_top_level_names()
    {
        var entries = new[]
        {
            Entry("App/Program.cs", index: GitChangeKind.Modified),
            Entry("App/Services/X.cs", index: GitChangeKind.Added),
            Entry("readme.md", index: GitChangeKind.Modified),
        };
        var view = new WorkspaceGitChangesView
        {
            WorkspaceId = 1,
            Repositories =
            [
                new WorkspaceGitChangesRepositoryView
                {
                    WorkspaceRepositoryId = 1,
                    RepositoryId = 1,
                    RepositoryName = "repo",
                    Changes = entries,
                },
            ],
        };

        // Staged section only (all entries above are staged).
        var rows = GitChangesTreeBuilder.Build(view, null);
        var topNames = rows
            .Where(r => r.Depth == 2 && r.IsStagedSection)
            .Select(r => r.Label)
            .ToList();

        var children = WorkspaceGitChangesReadService.OrderChildren(
            WorkspaceGitChangesReadService.GroupDirectChildren(
                string.Empty,
                entries.Where(e => e.IsStaged))).ToList();

        Assert.Equal(topNames, children.Select(c => c.Name).ToList());
    }

    [Fact]
    public void BuildFromCache_collapsed_repos_show_counts_without_files()
    {
        var view = GitChangesTreeBuilder.ViewFromSummaries(new WorkspaceGitChangesSummariesView
        {
            WorkspaceId = 1,
            TotalEntryCount = 2000,
            Repositories =
            [
                new WorkspaceGitChangesRepositorySummary
                {
                    WorkspaceRepositoryId = 7,
                    RepositoryId = 7,
                    RepositoryName = "big",
                    StagedCount = 0,
                    ChangedCount = 2000,
                    SnapshotVersion = 1,
                },
            ],
        });

        var collapsed = new HashSet<string>();
        GitChangesTreeBuilder.SeedCollapsedRepositories(view, collapsed);
        var cache = new GitChangesTreeNodeCache();
        var rows = GitChangesTreeBuilder.BuildFromCache(view, cache, collapsed);

        Assert.Contains(rows, r => r.Kind == GitChangesTreeRowKind.Section && r.Count == 2000);
        Assert.Contains(rows, r => r.Kind == GitChangesTreeRowKind.Repository && r.Count == 2000 && !r.IsExpanded);
        Assert.DoesNotContain(rows, r => r.Kind == GitChangesTreeRowKind.File);
    }

    [Fact]
    public void BuildFromCache_expanded_repo_hides_count_badge()
    {
        var view = GitChangesTreeBuilder.ViewFromSummaries(new WorkspaceGitChangesSummariesView
        {
            WorkspaceId = 1,
            TotalEntryCount = 10,
            Repositories =
            [
                new WorkspaceGitChangesRepositorySummary
                {
                    WorkspaceRepositoryId = 7,
                    RepositoryId = 7,
                    RepositoryName = "big",
                    StagedCount = 0,
                    ChangedCount = 10,
                    SnapshotVersion = 1,
                },
            ],
        });

        var collapsed = new HashSet<string>(); // repo expanded
        var rows = GitChangesTreeBuilder.BuildFromCache(view, new GitChangesTreeNodeCache(), collapsed);
        var repo = Assert.Single(rows, r => r.Kind == GitChangesTreeRowKind.Repository);
        Assert.True(repo.IsExpanded);
        Assert.Null(repo.Count);
    }

    [Fact]
    public void UseLargeHydration_threshold()
    {
        var options = new GitChangesOptions { FullHydrationMaxEntries = 1500 };
        Assert.False(options.UseLargeHydration(1500));
        Assert.True(options.UseLargeHydration(1501));
    }

    [Fact]
    public void BuildFromCache_loading_marks_repo_not_child_placeholder()
    {
        var view = GitChangesTreeBuilder.ViewFromSummaries(new WorkspaceGitChangesSummariesView
        {
            WorkspaceId = 1,
            TotalEntryCount = 2000,
            Repositories =
            [
                new WorkspaceGitChangesRepositorySummary
                {
                    WorkspaceRepositoryId = 7,
                    RepositoryId = 7,
                    RepositoryName = "big",
                    StagedCount = 0,
                    ChangedCount = 2000,
                    SnapshotVersion = 1,
                },
            ],
        });

        var collapsed = new HashSet<string>(); // repo expanded
        var cache = new GitChangesTreeNodeCache();
        cache.GetOrCreate(7, isStagedSection: false, string.Empty).IsLoading = true;

        var rows = GitChangesTreeBuilder.BuildFromCache(view, cache, collapsed);
        Assert.Contains(rows, r => r.Kind == GitChangesTreeRowKind.Repository && r.IsLoadingChildren);
        Assert.DoesNotContain(rows, r => r.Label.Contains("Loading", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildFromCache_child_folders_stay_collapsed_without_spinner()
    {
        var view = GitChangesTreeBuilder.ViewFromSummaries(new WorkspaceGitChangesSummariesView
        {
            WorkspaceId = 1,
            TotalEntryCount = 2000,
            Repositories =
            [
                new WorkspaceGitChangesRepositorySummary
                {
                    WorkspaceRepositoryId = 7,
                    RepositoryId = 7,
                    RepositoryName = "big",
                    StagedCount = 0,
                    ChangedCount = 10,
                    SnapshotVersion = 1,
                },
            ],
        });

        var collapsed = new HashSet<string> { "changed/7/src" };
        var cache = new GitChangesTreeNodeCache();
        var page = cache.GetOrCreate(7, isStagedSection: false, string.Empty);
        page.ApplyPage(new GitChangesEntryPage
        {
            SnapshotVersion = 1,
            TotalDirectChildCount = 1,
            Children =
            [
                new GitChangesTreeChild { IsFolder = true, Name = "src", Path = "src" },
            ],
            NextAfterChildKey = null,
        });

        var rows = GitChangesTreeBuilder.BuildFromCache(view, cache, collapsed);
        var folder = Assert.Single(rows, r => r.Kind == GitChangesTreeRowKind.Folder && r.Label == "src");
        Assert.False(folder.IsExpanded);
        Assert.False(folder.IsLoadingChildren);
    }

    [Fact]
    public void TreeNodeCache_ApplyPage_does_not_duplicate_children()
    {
        var cache = new GitChangesTreeNodeCache.PrefixPageCache();
        var page = new GitChangesEntryPage
        {
            SnapshotVersion = 3,
            TotalDirectChildCount = 1,
            Children =
            [
                new GitChangesTreeChild { IsFolder = false, Name = "a.txt", Path = "a.txt", Entry = new WorkspaceGitChangeEntryView { Path = "a.txt" } },
            ],
            NextAfterChildKey = null,
        };

        cache.ApplyPage(page);
        cache.ApplyPage(page);
        Assert.Single(cache.Children);
        Assert.True(cache.IsComplete);
    }
}
