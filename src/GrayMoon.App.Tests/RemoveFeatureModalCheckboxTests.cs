using GrayMoon.App.Components.Features;
using GrayMoon.Application.Features;

namespace GrayMoon.App.Tests;

/// <summary>
/// D3: the Remove dialog's checkboxes must only appear for risks that actually apply (fixes U-17,
/// where ticking only one checkbox could enable Remove while the real problem was the other risk,
/// causing Remove to fail with a raw Git error). These tests exercise the pure helpers extracted
/// from <see cref="RemoveFeatureModal"/> directly, covering every combination of the enable rule.
/// </summary>
public sealed class RemoveFeatureModalCheckboxTests
{
    private static RemoveFeatureRepositoryPlan MakeRepo(
        string name = "repo",
        bool hasUncommittedChanges = false,
        bool hasStagedChanges = false,
        bool hasConflicts = false,
        int? aheadOfDefault = 0,
        bool? pullRequestMerged = null) => new()
    {
        RepositoryName = name,
        WorktreeExists = true,
        LiveStatusEstablished = true,
        HasUncommittedChanges = hasUncommittedChanges,
        HasStagedChanges = hasStagedChanges,
        HasConflicts = hasConflicts,
        AheadOfDefault = aheadOfDefault,
        PullRequestMerged = pullRequestMerged,
    };

    [Fact]
    public void IsRepositoryDirty_true_for_uncommitted_changes()
    {
        var repo = MakeRepo(hasUncommittedChanges: true);

        Assert.True(RemoveFeatureModal.IsRepositoryDirty(repo));
    }

    [Fact]
    public void IsRepositoryDirty_true_for_staged_changes()
    {
        var repo = MakeRepo(hasStagedChanges: true);

        Assert.True(RemoveFeatureModal.IsRepositoryDirty(repo));
    }

    [Fact]
    public void IsRepositoryDirty_true_for_conflicts()
    {
        var repo = MakeRepo(hasConflicts: true);

        Assert.True(RemoveFeatureModal.IsRepositoryDirty(repo));
    }

    [Fact]
    public void IsRepositoryDirty_false_for_clean_repository()
    {
        var repo = MakeRepo();

        Assert.False(RemoveFeatureModal.IsRepositoryDirty(repo));
    }

    [Fact]
    public void HasUnmergedBranchAheadOfDefault_true_when_ahead_and_no_merged_pull_request()
    {
        var repo = MakeRepo(aheadOfDefault: 2, pullRequestMerged: null);

        Assert.True(RemoveFeatureModal.HasUnmergedBranchAheadOfDefault(repo));
    }

    [Fact]
    public void HasUnmergedBranchAheadOfDefault_true_when_ahead_and_pull_request_not_merged()
    {
        var repo = MakeRepo(aheadOfDefault: 1, pullRequestMerged: false);

        Assert.True(RemoveFeatureModal.HasUnmergedBranchAheadOfDefault(repo));
    }

    [Fact]
    public void HasUnmergedBranchAheadOfDefault_false_when_pull_request_merged()
    {
        var repo = MakeRepo(aheadOfDefault: 3, pullRequestMerged: true);

        Assert.False(RemoveFeatureModal.HasUnmergedBranchAheadOfDefault(repo));
    }

    [Fact]
    public void HasUnmergedBranchAheadOfDefault_false_when_not_ahead_of_default()
    {
        var repo = MakeRepo(aheadOfDefault: 0, pullRequestMerged: null);

        Assert.False(RemoveFeatureModal.HasUnmergedBranchAheadOfDefault(repo));
    }

    [Fact]
    public void HasUnmergedBranchAheadOfDefault_false_when_ahead_count_unknown()
    {
        var repo = MakeRepo(aheadOfDefault: null, pullRequestMerged: null);

        Assert.False(RemoveFeatureModal.HasUnmergedBranchAheadOfDefault(repo));
    }

    [Theory]
    [InlineData(false, false, false, false, false, true)]  // nothing shown, nothing ticked -> allowed
    [InlineData(false, true, true, false, false, true)]    // discard shown and ticked -> allowed
    [InlineData(false, true, false, false, false, false)]  // discard shown, not ticked -> blocked
    [InlineData(false, false, false, true, true, true)]    // force shown and ticked -> allowed
    [InlineData(false, false, false, true, false, false)]  // force shown, not ticked -> blocked
    [InlineData(false, true, true, true, true, true)]      // both shown and both ticked -> allowed
    [InlineData(false, true, true, true, false, false)]    // both shown, only discard ticked -> blocked
    [InlineData(false, true, false, true, true, false)]    // both shown, only force ticked -> blocked
    [InlineData(false, true, false, true, false, false)]   // both shown, neither ticked -> blocked
    [InlineData(true, false, false, false, false, false)]  // Unknown disk state always blocks, regardless of checkboxes
    [InlineData(true, true, true, true, true, false)]      // Unknown disk state blocks even if every shown checkbox is ticked
    public void CanRemove_matches_expected_combinations(
        bool anyRepoStatusUnknown,
        bool showDiscardCheckbox,
        bool allowDiscard,
        bool showForceCheckbox,
        bool allowForce,
        bool expected)
    {
        var actual = RemoveFeatureModal.CanRemove(
            anyRepoStatusUnknown, showDiscardCheckbox, allowDiscard, showForceCheckbox, allowForce);

        Assert.Equal(expected, actual);
    }
}
