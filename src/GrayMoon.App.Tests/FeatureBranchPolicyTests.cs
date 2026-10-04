using GrayMoon.App.Services.Features;

namespace GrayMoon.App.Tests;

/// <summary>I2: pure rules for a Feature's own branch (09 SB-1).</summary>
public sealed class FeatureBranchPolicyTests
{
    [Theory]
    [InlineData("feat-a", null, "feat-a")]
    [InlineData("feat-a", "1.2.0", null)]
    public void ExpectedBranch_returns_feature_name_unless_pinned_to_a_tag(
        string featureName, string? pinnedTag, string? expected)
    {
        Assert.Equal(expected, FeatureBranchPolicy.ExpectedBranch(featureName, pinnedTag));
    }

    [Theory]
    [InlineData("feat-a", "feat-a", false)]
    [InlineData("feat-a", "other-branch", true)]
    [InlineData("feat-a", null, true)]
    [InlineData(null, "feat-a", false)]
    [InlineData(null, null, false)]
    public void IsOffFeatureBranch_true_only_when_an_expected_branch_differs_from_current(
        string? expectedBranch, string? currentBranch, bool expected)
    {
        Assert.Equal(expected, FeatureBranchPolicy.IsOffFeatureBranch(expectedBranch, currentBranch));
    }
}
