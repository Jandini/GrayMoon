using GrayMoon.Common.Git;

namespace GrayMoon.Common.Tests;

public sealed class GitWorktreePorcelainParserTests
{
    [Fact]
    public void Empty_or_null_returns_empty()
    {
        Assert.Empty(GitWorktreePorcelainParser.Parse(null));
        Assert.Empty(GitWorktreePorcelainParser.Parse(""));
        Assert.Empty(GitWorktreePorcelainParser.Parse("   "));
    }

    [Fact]
    public void Parses_main_and_linked_worktrees()
    {
        var output =
            """
            worktree C:/repos/main
            HEAD abc111
            branch refs/heads/main

            worktree C:/repos/features/ABC-1/main
            HEAD def222
            branch refs/heads/ABC-1

            """;

        var list = GitWorktreePorcelainParser.Parse(output);
        Assert.Equal(2, list.Count);

        Assert.Equal("C:/repos/main", list[0].WorktreePath);
        Assert.Equal("abc111", list[0].HeadSha);
        Assert.Equal("refs/heads/main", list[0].BranchRef);
        Assert.Equal("main", list[0].BranchName);
        Assert.False(list[0].IsDetached);

        Assert.Equal("C:/repos/features/ABC-1/main", list[1].WorktreePath);
        Assert.Equal("ABC-1", list[1].BranchName);
        Assert.Equal("def222", list[1].HeadSha);
    }

    [Fact]
    public void Parses_detached_bare_and_prunable()
    {
        var output =
            """
            worktree C:/repos/detached
            HEAD aaa
            detached

            worktree C:/repos/bare
            bare

            worktree C:/repos/gone
            HEAD bbb
            branch refs/heads/gone
            prunable gitdir file points to non-existent location

            """;

        var list = GitWorktreePorcelainParser.Parse(output);
        Assert.Equal(3, list.Count);

        Assert.True(list[0].IsDetached);
        Assert.Null(list[0].BranchName);

        Assert.True(list[1].IsBare);

        Assert.True(list[2].IsPrunable);
        Assert.Equal("gitdir file points to non-existent location", list[2].PrunableReason);
        Assert.Equal("gone", list[2].BranchName);
    }

    [Fact]
    public void TryGetBranchName_only_heads_refs()
    {
        Assert.Equal("feat", GitWorktreePorcelainParser.TryGetBranchName("refs/heads/feat"));
        Assert.Null(GitWorktreePorcelainParser.TryGetBranchName("refs/tags/v1"));
        Assert.Null(GitWorktreePorcelainParser.TryGetBranchName(null));
    }
}
