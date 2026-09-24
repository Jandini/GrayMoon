using GrayMoon.Common.Git;

namespace GrayMoon.Common.Tests;

public sealed class GitWorktreeOccupancyTests
{
    private static readonly string Current = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "gm-wt-current"));
    private static readonly string Feature = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "gm-wt-feature"));
    private static readonly string External = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "gm-wt-external"));

    private static List<GitWorktreeInfo> SampleInventory() =>
    [
        new() { WorktreePath = Current, HeadSha = "aaa", BranchRef = "refs/heads/main", BranchName = "main" },
        new() { WorktreePath = Feature, HeadSha = "bbb", BranchRef = "refs/heads/ABC-1", BranchName = "ABC-1" },
        new() { WorktreePath = External, HeadSha = "ccc", BranchRef = "refs/heads/experiment", BranchName = "experiment" },
        new() { WorktreePath = Path.Combine(Path.GetTempPath(), "gm-wt-detached"), HeadSha = "ddd", IsDetached = true },
    ];

    [Fact]
    public void ClassifyBranch_current_occupied_and_none()
    {
        var inventory = SampleInventory();

        Assert.Equal(
            GitWorktreeBranchOccupancyKind.Current,
            GitWorktreeOccupancy.ClassifyBranch(inventory, "main", Current));

        Assert.Equal(
            GitWorktreeBranchOccupancyKind.OccupiedElsewhere,
            GitWorktreeOccupancy.ClassifyBranch(inventory, "ABC-1", Current));

        Assert.Equal(
            GitWorktreeBranchOccupancyKind.None,
            GitWorktreeOccupancy.ClassifyBranch(inventory, "free-branch", Current));
    }

    [Fact]
    public void FindByBranch_skips_detached()
    {
        var inventory = SampleInventory();
        Assert.Null(GitWorktreeOccupancy.FindByBranch(inventory, "HEAD"));
        Assert.Equal(Feature, GitWorktreeOccupancy.FindByBranch(inventory, "ABC-1")!.WorktreePath);
    }

    [Fact]
    public void FindByPath_normalizes()
    {
        var inventory = SampleInventory();
        var withTrailing = Feature.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        Assert.NotNull(GitWorktreeOccupancy.FindByPath(inventory, withTrailing));
    }

    [Fact]
    public void MatchesExpected_branch_and_optional_head()
    {
        var wt = new GitWorktreeInfo
        {
            WorktreePath = Feature,
            BranchName = "ABC-1",
            HeadSha = "bbb",
        };

        Assert.True(GitWorktreeOccupancy.MatchesExpected(wt, "ABC-1"));
        Assert.True(GitWorktreeOccupancy.MatchesExpected(wt, "ABC-1", "bbb"));
        Assert.False(GitWorktreeOccupancy.MatchesExpected(wt, "ABC-1", "zzz"));
        Assert.False(GitWorktreeOccupancy.MatchesExpected(wt, "other"));
    }
}
