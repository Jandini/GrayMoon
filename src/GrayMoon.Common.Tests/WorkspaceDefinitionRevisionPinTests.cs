using GrayMoon.Common.Git;

namespace GrayMoon.Common.Tests;

public sealed class WorkspaceDefinitionRevisionPinTests
{
    private const string Sha = "0123456789abcdef0123456789abcdef01234567";

    [Fact]
    public void Nothing_recorded_is_valid()
    {
        Assert.True(WorkspaceDefinitionRevisionPin.TryNormalize(null, " ", null, false, "Api", out var tag, out var branch, out var commit, out var error));
        Assert.Null(tag);
        Assert.Null(branch);
        Assert.Null(commit);
        Assert.Null(error);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_tag_pin_is_valid_whether_or_not_the_repository_is_read_only(bool isReference)
    {
        Assert.True(WorkspaceDefinitionRevisionPin.TryNormalize(" v1.0.0 ", null, Sha.ToUpperInvariant(), isReference, "Api", out var tag, out var branch, out var commit, out var error), error);
        Assert.Equal("v1.0.0", tag);
        Assert.Null(branch);
        Assert.Equal(Sha, commit);
    }

    [Fact]
    public void A_branch_with_a_commit_is_valid_for_a_read_only_repository()
    {
        Assert.True(WorkspaceDefinitionRevisionPin.TryNormalize(null, " main ", Sha.ToUpperInvariant(), true, "Api", out var tag, out var branch, out var commit, out var error), error);
        Assert.Null(tag);
        Assert.Equal("main", branch);
        Assert.Equal(Sha, commit);
    }

    [Theory]
    [InlineData(null, "main", Sha, false, "not read-only")]
    [InlineData("v1.0.0", "main", Sha, true, "both tag and branch")]
    [InlineData(null, "main", null, true, "missing the full commit hash")]
    [InlineData(null, "main", "0123456", true, "full commit hash")]
    [InlineData(null, "a b", Sha, true, "not a valid branch name")]
    [InlineData(null, null, Sha, true, "Api")]
    [InlineData("v1.0.0", null, null, true, "Api")]
    public void Every_other_combination_is_rejected_and_names_the_repository(string? tag, string? branch, string? commit, bool isReference, string expected)
    {
        Assert.False(WorkspaceDefinitionRevisionPin.TryNormalize(tag, branch, commit, isReference, "Api", out _, out _, out _, out var error));
        Assert.Contains("Api", error);
        Assert.Contains(expected, error);
    }

    [Theory]
    [InlineData("main", true)]
    [InlineData("feature/read-only", true)]
    [InlineData("release/1.x", true)]
    [InlineData("", false)]
    [InlineData("-x", false)]
    [InlineData("/x", false)]
    [InlineData("x/", false)]
    [InlineData("a b", false)]
    [InlineData("a..b", false)]
    [InlineData("a.lock", false)]
    [InlineData("a:b", false)]
    [InlineData("a\tb", false)]
    public void A_plain_branch_name_can_be_handed_to_git_as_one_argument(string name, bool expected) =>
        Assert.Equal(expected, WorkspaceDefinitionRevisionPin.IsPlainBranchName(name));
}
