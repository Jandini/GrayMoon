using GrayMoon.App.Services;
using GrayMoon.App.Services.GitChanges;
using GrayMoon.Common.Git;

namespace GrayMoon.App.Tests;

public class WorkspaceGitChangeSearchMatcherTests
{
    private static WorkspaceGitChangeEntryView Entry(
        string path,
        GitChangeKind index = GitChangeKind.None,
        GitChangeKind worktree = GitChangeKind.None) => new()
    {
        Path = path,
        IndexChange = index,
        WorktreeChange = worktree,
    };

    [Fact]
    public void Empty_query_matches_everything()
    {
        Assert.True(WorkspaceGitChangeSearchMatcher.Matches("repo-a", Entry("file.txt"), null));
        Assert.True(WorkspaceGitChangeSearchMatcher.Matches("repo-a", Entry("file.txt"), "  "));
    }

    [Fact]
    public void Plain_text_matches_path()
    {
        Assert.True(WorkspaceGitChangeSearchMatcher.Matches("repo-a", Entry("src/File.cs"), "File"));
        Assert.False(WorkspaceGitChangeSearchMatcher.Matches("repo-a", Entry("src/File.cs"), "zz"));
    }

    [Fact]
    public void Plain_text_matches_repository_name()
    {
        Assert.True(WorkspaceGitChangeSearchMatcher.Matches("graymoon-api", Entry("file.txt"), "graymoon"));
    }

    [Fact]
    public void Repo_field_matches_only_repository_name()
    {
        Assert.True(WorkspaceGitChangeSearchMatcher.Matches("graymoon-api", Entry("unrelated.txt"), "repo:api"));
        Assert.False(WorkspaceGitChangeSearchMatcher.Matches("graymoon-web", Entry("api.txt"), "repo:api"));
    }

    [Theory]
    [InlineData("status:modified", GitChangeKind.None, GitChangeKind.Modified, true)]
    [InlineData("status:added", GitChangeKind.Added, GitChangeKind.None, true)]
    [InlineData("status:deleted", GitChangeKind.None, GitChangeKind.Deleted, true)]
    [InlineData("status:modified", GitChangeKind.None, GitChangeKind.Added, false)]
    public void Status_field_matches_index_or_worktree_kind(string query, GitChangeKind index, GitChangeKind worktree, bool expected)
    {
        Assert.Equal(expected, WorkspaceGitChangeSearchMatcher.Matches("repo", Entry("file.txt", index, worktree), query));
    }

    [Fact]
    public void Staged_field_matches_staged_state()
    {
        var staged = Entry("a.txt", index: GitChangeKind.Added);
        var unstaged = Entry("b.txt", worktree: GitChangeKind.Modified);

        Assert.True(WorkspaceGitChangeSearchMatcher.Matches("repo", staged, "staged:true"));
        Assert.False(WorkspaceGitChangeSearchMatcher.Matches("repo", unstaged, "staged:true"));
        Assert.True(WorkspaceGitChangeSearchMatcher.Matches("repo", unstaged, "staged:false"));
    }

    [Fact]
    public void Ext_field_matches_file_extension()
    {
        Assert.True(WorkspaceGitChangeSearchMatcher.Matches("repo", Entry("src/File.cs"), "ext:cs"));
        Assert.False(WorkspaceGitChangeSearchMatcher.Matches("repo", Entry("src/File.cs"), "ext:ts"));
    }

    [Fact]
    public void And_and_or_operators_work_across_fields()
    {
        var entry = Entry("src/File.cs", worktree: GitChangeKind.Modified);

        Assert.True(WorkspaceGitChangeSearchMatcher.Matches("graymoon-api", entry, "repo:graymoon and ext:cs"));
        Assert.False(WorkspaceGitChangeSearchMatcher.Matches("graymoon-api", entry, "repo:graymoon and ext:ts"));
        Assert.True(WorkspaceGitChangeSearchMatcher.Matches("graymoon-api", entry, "ext:ts or ext:cs"));
    }
}
