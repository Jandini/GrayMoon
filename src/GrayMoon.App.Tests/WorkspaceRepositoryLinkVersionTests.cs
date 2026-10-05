using GrayMoon.App.Models;

namespace GrayMoon.App.Tests;

/// <summary>
/// The grid shows "unresolved" for a repository that has been synced (it has a branch or a tag) but has no
/// version, which is what a failed GitVersion leaves behind. A repository that was never synced has neither.
/// </summary>
public sealed class WorkspaceRepositoryLinkVersionTests
{
    [Theory]
    [InlineData(null, "main", null, true)]
    [InlineData("", "main", null, true)]
    [InlineData(null, null, "v1.0.0", true)]
    [InlineData("1.0.0", "main", null, false)]
    [InlineData(null, null, null, false)]
    [InlineData("", " ", null, false)]
    public void Version_is_unresolved_only_for_a_synced_repository_without_a_version(
        string? gitVersion, string? branch, string? tag, bool expected)
    {
        var link = new WorkspaceRepositoryLink { GitVersion = gitVersion, BranchName = branch, CheckedOutTag = tag };

        Assert.Equal(expected, link.IsVersionUnresolved);
    }
}
