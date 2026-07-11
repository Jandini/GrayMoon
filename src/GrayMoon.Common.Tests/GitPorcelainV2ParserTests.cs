using GrayMoon.Common.Git;

namespace GrayMoon.Common.Tests;

public class GitPorcelainV2ParserTests
{
    private static string Records(params string[] lines) => string.Join('\0', lines) + '\0';

    [Fact]
    public void Empty_output_returns_no_changes_and_default_branch()
    {
        var result = GitPorcelainV2Parser.Parse(string.Empty);

        Assert.Empty(result.Changes);
        Assert.Equal("HEAD", result.BranchName);
        Assert.Null(result.HeadCommit);
        Assert.False(result.IsDetachedHead);
        Assert.False(result.IsUnbornBranch);
    }

    [Fact]
    public void Null_output_returns_no_changes()
    {
        var result = GitPorcelainV2Parser.Parse(null);

        Assert.Empty(result.Changes);
    }

    [Fact]
    public void Parses_branch_name_and_head_commit()
    {
        var output = Records(
            "# branch.oid abc123def456",
            "# branch.head main");

        var result = GitPorcelainV2Parser.Parse(output);

        Assert.Equal("main", result.BranchName);
        Assert.Equal("abc123def456", result.HeadCommit);
        Assert.False(result.IsDetachedHead);
        Assert.False(result.IsUnbornBranch);
    }

    [Fact]
    public void Detached_head_sets_flag_and_branch_name()
    {
        var output = Records(
            "# branch.oid abc123def456",
            "# branch.head (detached)");

        var result = GitPorcelainV2Parser.Parse(output);

        Assert.True(result.IsDetachedHead);
        Assert.Equal("HEAD", result.BranchName);
    }

    [Fact]
    public void Unborn_branch_sets_flag_and_null_head_commit()
    {
        var output = Records(
            "# branch.oid (initial)",
            "# branch.head main");

        var result = GitPorcelainV2Parser.Parse(output);

        Assert.True(result.IsUnbornBranch);
        Assert.Null(result.HeadCommit);
        Assert.Equal("main", result.BranchName);
    }

    [Fact]
    public void Unstaged_modified_file()
    {
        var output = Records(
            "# branch.head main",
            "1 .M N... 100644 100644 100644 aaaa bbbb file.txt");

        var change = Assert.Single(GitPorcelainV2Parser.Parse(output).Changes);

        Assert.Equal("file.txt", change.Path);
        Assert.Equal(GitChangeKind.None, change.IndexChange);
        Assert.Equal(GitChangeKind.Modified, change.WorktreeChange);
        Assert.True(change.IsTracked);
        Assert.False(change.IsConflicted);
        Assert.False(change.IsSubmodule);
    }

    [Fact]
    public void Staged_added_file()
    {
        var output = Records(
            "# branch.head main",
            "1 A. N... 000000 100644 100644 0000 bbbb newfile.txt");

        var change = Assert.Single(GitPorcelainV2Parser.Parse(output).Changes);

        Assert.Equal(GitChangeKind.Added, change.IndexChange);
        Assert.Equal(GitChangeKind.None, change.WorktreeChange);
        Assert.True(change.IsStaged);
        Assert.False(change.IsChanged);
    }

    [Fact]
    public void File_with_both_staged_and_unstaged_modification()
    {
        var output = Records(
            "# branch.head main",
            "1 MM N... 100644 100644 100644 aaaa bbbb both.txt");

        var change = Assert.Single(GitPorcelainV2Parser.Parse(output).Changes);

        Assert.Equal(GitChangeKind.Modified, change.IndexChange);
        Assert.Equal(GitChangeKind.Modified, change.WorktreeChange);
        Assert.True(change.IsStaged);
        Assert.True(change.IsChanged);
    }

    [Fact]
    public void Staged_deleted_file()
    {
        var output = Records(
            "# branch.head main",
            "1 D. N... 100644 000000 000000 aaaa 0000 removed.txt");

        var change = Assert.Single(GitPorcelainV2Parser.Parse(output).Changes);

        Assert.Equal(GitChangeKind.Deleted, change.IndexChange);
        Assert.Equal(GitChangeKind.None, change.WorktreeChange);
    }

    [Fact]
    public void Untracked_file_is_not_tracked_and_has_no_index_change()
    {
        var output = Records(
            "# branch.head main",
            "? untracked.txt");

        var change = Assert.Single(GitPorcelainV2Parser.Parse(output).Changes);

        Assert.Equal("untracked.txt", change.Path);
        Assert.False(change.IsTracked);
        Assert.Equal(GitChangeKind.None, change.IndexChange);
        Assert.Equal(GitChangeKind.Untracked, change.WorktreeChange);
    }

    [Fact]
    public void Rename_record_captures_original_and_new_path()
    {
        var output = Records(
            "# branch.head main",
            "2 R. N... 100644 100644 100644 aaaa bbbb R100 new_name.txt",
            "old_name.txt");

        var change = Assert.Single(GitPorcelainV2Parser.Parse(output).Changes);

        Assert.Equal("new_name.txt", change.Path);
        Assert.Equal("old_name.txt", change.OriginalPath);
        Assert.Equal(GitChangeKind.Renamed, change.IndexChange);
    }

    [Fact]
    public void Rename_record_does_not_consume_the_following_unrelated_record()
    {
        var output = Records(
            "# branch.head main",
            "2 R. N... 100644 100644 100644 aaaa bbbb R100 renamed.txt",
            "original.txt",
            "1 .M N... 100644 100644 100644 aaaa bbbb other.txt");

        var changes = GitPorcelainV2Parser.Parse(output).Changes;

        Assert.Equal(2, changes.Count);
        Assert.Equal("renamed.txt", changes[0].Path);
        Assert.Equal("original.txt", changes[0].OriginalPath);
        Assert.Equal("other.txt", changes[1].Path);
        Assert.Null(changes[1].OriginalPath);
    }

    [Fact]
    public void Conflicted_unmerged_entry_is_marked_conflicted()
    {
        var output = Records(
            "# branch.head main",
            "u UU N... 100644 100644 100644 100644 aaaa bbbb cccc conflicted.txt");

        var change = Assert.Single(GitPorcelainV2Parser.Parse(output).Changes);

        Assert.Equal("conflicted.txt", change.Path);
        Assert.True(change.IsConflicted);
        Assert.Equal(GitChangeKind.Unmerged, change.IndexChange);
        Assert.Equal(GitChangeKind.Unmerged, change.WorktreeChange);
    }

    [Fact]
    public void Submodule_entry_sets_is_submodule()
    {
        var output = Records(
            "# branch.head main",
            "1 .M S... 160000 160000 160000 aaaa bbbb sub-module");

        var change = Assert.Single(GitPorcelainV2Parser.Parse(output).Changes);

        Assert.True(change.IsSubmodule);
    }

    [Fact]
    public void Path_with_spaces_is_preserved_verbatim()
    {
        var output = Records(
            "# branch.head main",
            "1 .M N... 100644 100644 100644 aaaa bbbb src/my file with spaces.txt");

        var change = Assert.Single(GitPorcelainV2Parser.Parse(output).Changes);

        Assert.Equal("src/my file with spaces.txt", change.Path);
    }

    [Fact]
    public void Path_with_unicode_is_preserved_verbatim()
    {
        var output = Records(
            "# branch.head main",
            "1 .M N... 100644 100644 100644 aaaa bbbb 文件夹/文件.txt");

        var change = Assert.Single(GitPorcelainV2Parser.Parse(output).Changes);

        Assert.Equal("文件夹/文件.txt", change.Path);
    }

    [Fact]
    public void Multiple_repositories_worth_of_entries_all_parse_independently()
    {
        var output = Records(
            "# branch.oid abc123",
            "# branch.head feature/one",
            "1 M. N... 100644 100644 100644 aaaa bbbb a.txt",
            "1 .M N... 100644 100644 100644 aaaa bbbb b.txt",
            "? new.txt",
            "u UU N... 100644 100644 100644 100644 aaaa bbbb cccc conflict.txt");

        var result = GitPorcelainV2Parser.Parse(output);

        Assert.Equal("feature/one", result.BranchName);
        Assert.Equal(4, result.Changes.Count);
    }

    [Fact]
    public void Ignored_entries_are_skipped_without_affecting_other_records()
    {
        var output = Records(
            "# branch.head main",
            "! bin/",
            "1 .M N... 100644 100644 100644 aaaa bbbb tracked.txt");

        var changes = GitPorcelainV2Parser.Parse(output).Changes;

        var change = Assert.Single(changes);
        Assert.Equal("tracked.txt", change.Path);
    }
}
