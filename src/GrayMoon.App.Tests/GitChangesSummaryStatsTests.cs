using GrayMoon.App.Services.GitChanges;
using GrayMoon.Common.Git;

namespace GrayMoon.App.Tests;

public class GitChangesSummaryStatsTests
{
    [Fact]
    public void Null_view_is_empty()
    {
        var stats = GitChangesSummaryStats.From(null);

        Assert.Empty(stats.Kinds);
        Assert.Equal(0, stats.Insertions);
        Assert.Equal(0, stats.Deletions);
        Assert.False(stats.HasLineStats);
    }

    [Fact]
    public void Unstaged_modified_counts_as_M()
    {
        var stats = FromEntries(Entry("a.cs", worktree: GitChangeKind.Modified));

        var chip = Assert.Single(stats.Kinds);
        Assert.Equal("M", chip.Letter);
        Assert.Equal("modified", chip.StatusClass);
        Assert.Equal(1, chip.Count);
    }

    [Fact]
    public void Staging_a_changed_file_removes_it_from_kind_chips()
    {
        var before = FromEntries(Entry("a.cs", worktree: GitChangeKind.Modified));
        Assert.Equal(1, Assert.Single(before.Kinds).Count);

        var after = FromEntries(Entry("a.cs", index: GitChangeKind.Modified));
        Assert.Empty(after.Kinds);

        var unstaged = FromEntries(Entry("a.cs", worktree: GitChangeKind.Modified));
        Assert.Equal("M", Assert.Single(unstaged.Kinds).Letter);
    }

    [Fact]
    public void Staged_only_added_is_hidden_until_unstaged()
    {
        var stats = FromEntries(Entry("a.cs", index: GitChangeKind.Added));

        Assert.Empty(stats.Kinds);
    }

    [Fact]
    public void File_with_both_index_and_worktree_counts_the_changed_side()
    {
        var stats = FromEntries(Entry("a.cs", index: GitChangeKind.Added, worktree: GitChangeKind.Modified));

        Assert.Equal("M", Assert.Single(stats.Kinds).Letter);
    }

    [Fact]
    public void Conflict_counts_as_U_even_when_worktree_is_modified()
    {
        var stats = FromEntries(new WorkspaceGitChangeEntryView
        {
            Path = "a.cs",
            IndexChange = GitChangeKind.Unmerged,
            WorktreeChange = GitChangeKind.Modified,
            IsConflicted = true,
        });

        var chip = Assert.Single(stats.Kinds);
        Assert.Equal("U", chip.Letter);
        Assert.Equal("conflict", chip.StatusClass);
    }

    [Fact]
    public void Untracked_counts_on_the_A_chip()
    {
        var stats = FromEntries(Entry("new.cs", worktree: GitChangeKind.Untracked));

        var chip = Assert.Single(stats.Kinds);
        Assert.Equal("A", chip.Letter);
        Assert.Equal("added", chip.StatusClass);
    }

    [Fact]
    public void Zero_count_kinds_are_hidden_and_display_order_is_M_A_D_R_C_U_T()
    {
        var stats = FromEntries(
            Entry("m.cs", worktree: GitChangeKind.Modified),
            Entry("t.cs", worktree: GitChangeKind.TypeChanged),
            Entry("r.cs", worktree: GitChangeKind.Renamed),
            Entry("a.cs", worktree: GitChangeKind.Added));

        Assert.Equal(["M", "A", "R", "T"], stats.Kinds.Select(k => k.Letter).ToArray());
    }

    [Fact]
    public void Line_totals_sum_across_repositories_and_treat_null_as_zero()
    {
        var view = new WorkspaceGitChangesView
        {
            WorkspaceId = 1,
            Repositories =
            [
                RepoWithLines(1, "a", 10, 2),
                RepoWithLines(2, "b", null, null),
                RepoWithLines(3, "c", 1, 4),
            ],
        };

        var stats = GitChangesSummaryStats.From(view);

        Assert.Equal(11, stats.Insertions);
        Assert.Equal(6, stats.Deletions);
        Assert.True(stats.HasLineStats);
    }

    [Fact]
    public void Line_totals_use_unstaged_fields_only()
    {
        var view = new WorkspaceGitChangesView
        {
            WorkspaceId = 1,
            Repositories =
            [
                RepoWithLines(1, "a", 10, 2, stagedInsertions: 4, stagedDeletions: 1),
            ],
        };

        var stats = GitChangesSummaryStats.From(view);
        Assert.Equal(10, stats.Insertions);
        Assert.Equal(2, stats.Deletions);
        Assert.True(stats.HasLineStats);
    }

    [Fact]
    public void Computed_zero_line_stats_still_count_as_present()
    {
        var view = new WorkspaceGitChangesView
        {
            WorkspaceId = 1,
            Repositories = [RepoWithLines(1, "a", 0, 0)],
        };

        var stats = GitChangesSummaryStats.From(view);

        Assert.True(stats.HasLineStats);
        Assert.Equal(0, stats.Insertions);
        Assert.Equal(0, stats.Deletions);
    }

    [Fact]
    public void Copied_and_deleted_use_tree_status_classes()
    {
        var stats = FromEntries(
            Entry("c.cs", worktree: GitChangeKind.Copied),
            Entry("d.cs", worktree: GitChangeKind.Deleted));

        Assert.Equal("modified", stats.Kinds.Single(k => k.Letter == "C").StatusClass);
        Assert.Equal("deleted", stats.Kinds.Single(k => k.Letter == "D").StatusClass);
    }

    private static GitChangesSummaryStats FromEntries(params WorkspaceGitChangeEntryView[] entries)
    {
        var view = new WorkspaceGitChangesView
        {
            WorkspaceId = 1,
            Repositories = [Repo(1, "repo", entries)],
        };
        return GitChangesSummaryStats.From(view);
    }

    private static WorkspaceGitChangeEntryView Entry(
        string path,
        GitChangeKind index = GitChangeKind.None,
        GitChangeKind worktree = GitChangeKind.None) => new()
    {
        Path = path,
        IndexChange = index,
        WorktreeChange = worktree,
    };

    private static WorkspaceGitChangesRepositoryView Repo(int id, string name, params WorkspaceGitChangeEntryView[] changes) => new()
    {
        WorkspaceRepositoryId = id,
        RepositoryId = id,
        RepositoryName = name,
        Changes = changes,
    };

    private static WorkspaceGitChangesRepositoryView RepoWithLines(
        int id,
        string name,
        int? insertions,
        int? deletions,
        int? stagedInsertions = null,
        int? stagedDeletions = null) => new()
    {
        WorkspaceRepositoryId = id,
        RepositoryId = id,
        RepositoryName = name,
        Insertions = insertions,
        Deletions = deletions,
        StagedInsertions = stagedInsertions,
        StagedDeletions = stagedDeletions,
        Changes = [],
    };
}
