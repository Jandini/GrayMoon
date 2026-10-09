using GrayMoon.App.Components.Features;
using GrayMoon.Application;
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
        bool? pullRequestMerged = null,
        bool isLocked = false,
        string? lockReason = null,
        bool hasUpstream = false,
        int? outgoingCommits = 0) => new()
    {
        RepositoryName = name,
        WorktreeExists = true,
        LiveStatusEstablished = true,
        HasUncommittedChanges = hasUncommittedChanges,
        HasStagedChanges = hasStagedChanges,
        HasConflicts = hasConflicts,
        AheadOfDefault = aheadOfDefault,
        PullRequestMerged = pullRequestMerged,
        IsLocked = isLocked,
        LockReason = lockReason,
        HasUpstream = hasUpstream,
        OutgoingCommits = outgoingCommits,
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

    [Fact]
    public void HasLockedWorktree_true_when_locked()
    {
        var repo = MakeRepo(isLocked: true, lockReason: "testing");

        Assert.True(RemoveFeatureModal.HasLockedWorktree(repo));
    }

    [Fact]
    public void HasLockedWorktree_false_when_not_locked()
    {
        var repo = MakeRepo();

        Assert.False(RemoveFeatureModal.HasLockedWorktree(repo));
    }

    [Theory]
    [InlineData(false, false, false, false, false, false, false, true)]  // nothing shown, nothing ticked -> allowed
    [InlineData(false, true, true, false, false, false, false, true)]    // discard shown and ticked -> allowed
    [InlineData(false, true, false, false, false, false, false, false)]  // discard shown, not ticked -> blocked
    [InlineData(false, false, false, true, true, false, false, true)]    // force shown and ticked -> allowed
    [InlineData(false, false, false, true, false, false, false, false)]  // force shown, not ticked -> blocked
    [InlineData(false, true, true, true, true, false, false, true)]      // both shown and both ticked -> allowed
    [InlineData(false, true, true, true, false, false, false, false)]    // both shown, only discard ticked -> blocked
    [InlineData(false, true, false, true, true, false, false, false)]    // both shown, only force ticked -> blocked
    [InlineData(false, true, false, true, false, false, false, false)]   // both shown, neither ticked -> blocked
    [InlineData(true, false, false, false, false, false, false, false)]  // Unknown disk state always blocks, regardless of checkboxes
    [InlineData(true, true, true, true, true, true, true, false)]        // Unknown disk state blocks even if every shown checkbox is ticked
    [InlineData(false, false, false, false, false, true, true, true)]    // unlock shown and ticked -> allowed (D5)
    [InlineData(false, false, false, false, false, true, false, false)]  // unlock shown, not ticked -> blocked (D5)
    [InlineData(false, true, true, true, true, true, true, true)]        // discard, force and unlock all shown and ticked -> allowed (D5)
    [InlineData(false, true, true, true, true, true, false, false)]      // discard and force ticked, unlock not ticked -> blocked (D5)
    public void CanRemove_matches_expected_combinations(
        bool anyRepoStatusUnknown,
        bool showDiscardCheckbox,
        bool allowDiscard,
        bool showForceCheckbox,
        bool allowForce,
        bool showUnlockCheckbox,
        bool allowUnlock,
        bool expected)
    {
        var actual = RemoveFeatureModal.CanRemove(
            anyRepoStatusUnknown, showDiscardCheckbox, allowDiscard, showForceCheckbox, allowForce,
            showUnlockCheckbox, allowUnlock);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void HasReportWarnings_false_for_clean_report()
    {
        IReadOnlyList<RemoveFeatureRepositoryReport> report =
        [
            new(1, "repo", WorktreeRemoved: true, RemoveFeatureBranchOutcome.Deleted, null,
                ResidueRemaining: false, ResidueFileCount: 0, null, null)
        ];

        Assert.False(RemoveFeatureModal.HasReportWarnings(report));
    }

    [Fact]
    public void HasReportWarnings_true_for_kept_unmerged_branch()
    {
        IReadOnlyList<RemoveFeatureRepositoryReport> report =
        [
            new(1, "repo", WorktreeRemoved: true, RemoveFeatureBranchOutcome.KeptUnmerged, "not fully merged",
                ResidueRemaining: false, ResidueFileCount: 0, null, null)
        ];

        Assert.True(RemoveFeatureModal.HasReportWarnings(report));
    }

    [Fact]
    public void Leftover_files_are_not_a_warning_but_a_kept_branch_name_is()
    {
        // Leftover files only mark the folder pending deletion (information, cleaned up later), never a warning.
        IReadOnlyList<RemoveFeatureRepositoryReport> residue =
        [
            new(1, "repo", WorktreeRemoved: true, RemoveFeatureBranchOutcome.Deleted, null,
                ResidueRemaining: true, ResidueFileCount: 1, ["a.lock"], "still open")
        ];
        IReadOnlyList<RemoveFeatureRepositoryReport> keptBranch =
        [
            new(1, "repo", WorktreeRemoved: true, RemoveFeatureBranchOutcome.Deleted, null,
                ResidueRemaining: false, ResidueFileCount: 0, null, null, KeptBranchName: "side")
        ];

        Assert.False(RemoveFeatureModal.HasReportWarnings(residue));
        Assert.Empty(RemoveFeatureModal.BuildReportWarnings(residue));
        Assert.True(RemoveFeatureModal.HasReportWarnings(keptBranch));
        Assert.Single(RemoveFeatureModal.BuildReportWarnings(keptBranch));
    }

    [Fact]
    public void HasReportWarnings_false_for_null_or_empty()
    {
        Assert.False(RemoveFeatureModal.HasReportWarnings(null));
        Assert.False(RemoveFeatureModal.HasReportWarnings([]));
    }

    [Fact]
    public void IsRepositoryNothingPending_true_for_clean_repo()
    {
        Assert.True(RemoveFeatureModal.IsRepositoryNothingPending(MakeRepo()));
    }

    [Fact]
    public void IsRepositoryNothingPending_false_when_dirty_or_ahead()
    {
        Assert.False(RemoveFeatureModal.IsRepositoryNothingPending(MakeRepo(hasUncommittedChanges: true)));
        Assert.False(RemoveFeatureModal.IsRepositoryNothingPending(MakeRepo(aheadOfDefault: 2)));
    }

    [Fact]
    public void RemoveCtaIsDestructive_false_when_automatically_safe()
    {
        var plan = new RemoveFeaturePlan
        {
            Success = true,
            Classification = RemoveFeatureClassification.Completed,
            IsAutomaticallySafe = true,
            Repositories = [MakeRepo()]
        };

        Assert.False(RemoveFeatureModal.RemoveCtaIsDestructive(plan));
    }

    [Fact]
    public void RemoveCtaIsDestructive_true_when_not_automatically_safe()
    {
        var plan = new RemoveFeaturePlan
        {
            Success = true,
            Classification = RemoveFeatureClassification.Active,
            IsAutomaticallySafe = false,
            Repositories = [MakeRepo(aheadOfDefault: 1)]
        };

        Assert.True(RemoveFeatureModal.RemoveCtaIsDestructive(plan));
    }

    [Fact]
    public void HasRemoteFeatureBranchToDelete_requires_upstream()
    {
        var withUpstream = new RemoveFeatureRepositoryPlan
        {
            RepositoryName = "repo",
            FeatureBranchName = "feat",
            FeatureBranchHasUpstream = true
        };
        var without = new RemoveFeatureRepositoryPlan
        {
            RepositoryName = "repo",
            FeatureBranchName = "feat",
            FeatureBranchHasUpstream = false
        };

        Assert.True(RemoveFeatureModal.HasRemoteFeatureBranchToDelete(withUpstream));
        Assert.False(RemoveFeatureModal.HasRemoteFeatureBranchToDelete(without));
    }

    [Fact]
    public void HasLocalFeatureBranchToDelete_false_when_branch_already_gone()
    {
        var gone = new RemoveFeatureRepositoryPlan
        {
            RepositoryName = "repo",
            FeatureBranchName = "feat",
            FeatureBranchExists = false
        };

        Assert.False(RemoveFeatureModal.HasLocalFeatureBranchToDelete(gone));
    }

    [Fact]
    public void BuildRepositoryStatusGroups_summarizes_identical_no_pull_request_status()
    {
        var repos = new[]
        {
            MakeRepo(name: "MezzoRecovery", hasUpstream: true),
            MakeRepo(name: "MezzoRecovery.Agent", hasUpstream: true),
            MakeRepo(name: "MezzoRecovery.App", hasUpstream: true),
        };

        var groups = RemoveFeatureModal.BuildRepositoryStatusGroups(repos);

        Assert.Single(groups);
        Assert.Equal(RemoveFeatureModal.NoPullRequestStatusMessage, groups[0].Message);
        Assert.Equal(3, groups[0].Count);
        Assert.True(RemoveFeatureModal.ShouldSummarizeStatusGroup(groups[0]));
        Assert.Equal(
            "All 3 repositories have no pull request opened for this branch.",
            RemoveFeatureModal.FormatStatusGroupSummary(
                groups[0].Message, groups[0].Count, allRepositoriesShareThisStatus: true));
    }

    [Fact]
    public void BuildRepositoryStatusGroups_mixed_clean_and_no_pull_request_yields_group_summaries()
    {
        var repos = new[]
        {
            MakeRepo(name: "pushed-a", hasUpstream: true),
            MakeRepo(name: "pushed-b", hasUpstream: true),
            MakeRepo(name: "clean-a"),
            MakeRepo(name: "clean-b"),
            MakeRepo(name: "clean-c"),
        };

        var groups = RemoveFeatureModal.BuildRepositoryStatusGroups(repos);

        Assert.Equal(2, groups.Count);
        Assert.Equal(RemoveFeatureModal.NoPullRequestStatusMessage, groups[0].Message);
        Assert.Equal(2, groups[0].Count);
        Assert.Equal(RemoveFeatureModal.NothingPendingStatusMessage, groups[1].Message);
        Assert.Equal(3, groups[1].Count);
        Assert.True(RemoveFeatureModal.ShouldSummarizeStatusGroup(groups[0]));
        Assert.True(RemoveFeatureModal.ShouldSummarizeStatusGroup(groups[1]));
        Assert.Equal(
            "2 repositories have no pull request opened for this branch.",
            RemoveFeatureModal.FormatStatusGroupSummary(
                groups[0].Message, groups[0].Count, allRepositoriesShareThisStatus: false));
        Assert.Equal(
            "3 other repositories are up to date with nothing pending.",
            RemoveFeatureModal.FormatStatusGroupSummary(
                groups[1].Message, groups[1].Count, allRepositoriesShareThisStatus: false));
    }

    [Fact]
    public void BuildRepositoryStatusGroups_keeps_unique_status_as_detail_and_summarizes_clean()
    {
        var repos = new[]
        {
            MakeRepo(name: "dirty", hasUncommittedChanges: true),
            MakeRepo(name: "clean-a"),
            MakeRepo(name: "clean-b"),
        };

        var groups = RemoveFeatureModal.BuildRepositoryStatusGroups(repos);

        Assert.Equal(2, groups.Count);
        Assert.False(RemoveFeatureModal.ShouldSummarizeStatusGroup(groups[0]));
        Assert.Equal("dirty", groups[0].Repositories[0].RepositoryName);
        Assert.Equal("Has uncommitted changes", groups[0].Message);
        Assert.True(RemoveFeatureModal.ShouldSummarizeStatusGroup(groups[1]));
        Assert.Equal(
            "2 other repositories are up to date with nothing pending.",
            RemoveFeatureModal.FormatStatusGroupSummary(
                groups[1].Message, groups[1].Count, allRepositoriesShareThisStatus: false));
    }

    [Fact]
    public void BuildRepositoryStatusGroups_keeps_per_repo_detail_when_commit_counts_differ()
    {
        var repos = new[]
        {
            MakeRepo(name: "one-out", outgoingCommits: 1),
            MakeRepo(name: "two-out", outgoingCommits: 2),
        };

        var groups = RemoveFeatureModal.BuildRepositoryStatusGroups(repos);

        Assert.Equal(2, groups.Count);
        Assert.All(groups, g => Assert.False(RemoveFeatureModal.ShouldSummarizeStatusGroup(g)));
        Assert.Equal("1 commit not pushed to the remote", groups[0].Message);
        Assert.Equal("2 commits not pushed to the remote", groups[1].Message);
    }

    [Fact]
    public void ShouldSummarizeStatusGroup_true_for_single_clean_repo()
    {
        var group = new RemoveFeatureModal.RepositoryStatusGroup(
            RemoveFeatureModal.NothingPendingStatusMessage,
            Count: 1,
            [MakeRepo(name: "clean")]);

        Assert.True(RemoveFeatureModal.ShouldSummarizeStatusGroup(group));
    }

    [Fact]
    public void FormatStatusGroupSummary_other_clean_when_mixed_with_summaries_only()
    {
        var text = RemoveFeatureModal.FormatStatusGroupSummary(
            RemoveFeatureModal.NothingPendingStatusMessage,
            count: 1,
            allRepositoriesShareThisStatus: false);

        Assert.Equal("1 other repository is up to date with nothing pending.", text);
    }
}
