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

    [Theory]
    // On its Feature branch: not drifted.
    [InlineData("feat-a", null, "feat-a", true, null)]
    // Another branch: drifted, returns the Feature branch to go back to.
    [InlineData("feat-a", null, "main", true, "feat-a")]
    [InlineData("feat-a", null, "Feat-A", true, "feat-a")]
    // Blank branch with a recorded state is a detached HEAD: drifted.
    [InlineData("feat-a", null, null, true, "feat-a")]
    [InlineData("feat-a", null, "", true, "feat-a")]
    // Blank branch before the first sync is unknown: not flagged.
    [InlineData("feat-a", null, null, false, null)]
    // Tag-pinned repository: nothing is expected, so nothing is flagged.
    [InlineData("feat-a", "1.0.0", null, true, null)]
    [InlineData("feat-a", "1.0.0", "main", true, null)]
    // Workspace (no Feature name): never flagged.
    [InlineData(null, null, "main", true, null)]
    [InlineData(null, null, null, true, null)]
    public void GetOffFeatureBranch_flags_only_a_non_pinned_Feature_repository_off_its_branch(
        string? featureName, string? pinnedTag, string? currentBranch, bool hasRecordedState, string? expected)
    {
        Assert.Equal(expected, FeatureBranchPolicy.GetOffFeatureBranch(featureName, pinnedTag, currentBranch, hasRecordedState));
    }

    [Theory]
    // Branch checkout: only the expected branch is allowed.
    [InlineData(FeatureBranchAction.Checkout, "feat-a", null, "feat-a", false, true)]
    [InlineData(FeatureBranchAction.Checkout, "feat-a", null, "main", false, false)]
    [InlineData(FeatureBranchAction.Checkout, "feat-a", null, "Feat-A", false, false)]
    // A repository pinned to a tag has no expected branch, so no branch is allowed.
    [InlineData(FeatureBranchAction.Checkout, null, "1.0.0", "feat-a", false, false)]
    // Tag checkout: only for a pinned repository.
    [InlineData(FeatureBranchAction.Checkout, null, "1.0.0", "2.0.0", true, true)]
    [InlineData(FeatureBranchAction.Checkout, "feat-a", null, "2.0.0", true, false)]
    // Create and Return to Default are never allowed.
    [InlineData(FeatureBranchAction.CreateBranch, "feat-a", null, "other", false, false)]
    [InlineData(FeatureBranchAction.ReturnToDefault, "feat-a", null, "feat-a", false, false)]
    public void Evaluate_allows_only_actions_that_keep_the_repository_on_its_Feature_branch(
        FeatureBranchAction action, string? expectedBranch, string? pinnedTag, string? target, bool isTag, bool allowed)
    {
        var message = FeatureBranchPolicy.Evaluate(action, expectedBranch, pinnedTag, target, isTag);

        Assert.Equal(allowed, message is null);
    }

    [Fact]
    public void Evaluate_uses_the_documented_messages()
    {
        Assert.Equal(FeatureBranchPolicy.TagNotPinnedMessage,
            FeatureBranchPolicy.Evaluate(FeatureBranchAction.Checkout, "feat-a", null, "1.0.0", isTag: true));
        Assert.Equal(FeatureBranchPolicy.CreateBranchMessage,
            FeatureBranchPolicy.Evaluate(FeatureBranchAction.CreateBranch, "feat-a", null, "x", isTag: false));
        Assert.Equal("Return to Default is a Workspace action.",
            FeatureBranchPolicy.Evaluate(FeatureBranchAction.ReturnToDefault, "feat-a", null, null, isTag: false));
    }
}
