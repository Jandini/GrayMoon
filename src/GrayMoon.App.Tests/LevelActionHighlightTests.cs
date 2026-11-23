using GrayMoon.App.Components.Shared;
using GrayMoon.App.Models;

namespace GrayMoon.App.Tests;

public sealed class LevelActionHighlightTests
{
    [Fact]
    public void SyncCommits_TrueWhenIncomingOrOutgoingIsAtLeastOne()
    {
        Assert.True(CommitsBadge.HasIncomingOrOutgoingCommits(false, true, outgoing: 1, incoming: 0));
        Assert.True(CommitsBadge.HasIncomingOrOutgoingCommits(false, true, outgoing: 0, incoming: 1));
    }

    [Fact]
    public void SyncCommits_FalseWhenBothCountsAreZero()
    {
        Assert.False(CommitsBadge.HasIncomingOrOutgoingCommits(false, true, outgoing: 0, incoming: 0));
        Assert.False(CommitsBadge.HasIncomingOrOutgoingCommits(false, true, outgoing: null, incoming: null));
    }

    [Fact]
    public void SyncCommits_FalseWhenBranchHasNoUpstream()
    {
        Assert.False(CommitsBadge.HasIncomingOrOutgoingCommits(false, false, outgoing: 3, incoming: 2));
    }

    [Fact]
    public void SyncCommits_FalseWhenOnTag()
    {
        Assert.False(CommitsBadge.HasIncomingOrOutgoingCommits(true, true, outgoing: 2, incoming: 2));
    }

    [Theory]
    [InlineData(0, 0, true, "#6c757d", "#fff")]  // no upstream, no commits: gray, a push skips it
    [InlineData(2, 0, true, "#ffc107", "#000")]  // no upstream with commits: yellow, a push sends it
    [InlineData(2, 0, false, "#ffc107", "#000")] // outgoing: yellow
    [InlineData(0, 1, false, "#dc3545", "#fff")] // incoming: red
    [InlineData(0, 0, false, "#198754", "#fff")] // in sync: green
    public void Commits_badge_is_gray_for_a_branch_without_upstream_and_commits(int outgoing, int incoming, bool notUpstreamed, string background, string text)
    {
        Assert.Equal((background, text), CommitsBadge.DetermineColors(outgoing, incoming, notUpstreamed));
    }

    [Fact]
    public void CreatePrs_TrueWhenVerifiedAheadAndNoPr()
    {
        Assert.True(PRBadge.ShowsCreateBadge(false, true, pullRequest: null, defaultBranchAheadCommits: 1));
    }

    [Fact]
    public void CreatePrs_FalseWhenPrAlreadyExists()
    {
        Assert.False(PRBadge.ShowsCreateBadge(false, true, OpenPr(), defaultBranchAheadCommits: 4));
        Assert.False(PRBadge.ShowsCreateBadge(false, true, MergedPr(), defaultBranchAheadCommits: 4));
        Assert.False(PRBadge.ShowsCreateBadge(false, true, ClosedPr(), defaultBranchAheadCommits: 4));
    }

    [Fact]
    public void CreatePrs_FalseWhenNotVerifiedOrNotAhead()
    {
        Assert.False(PRBadge.ShowsCreateBadge(false, false, pullRequest: null, defaultBranchAheadCommits: 4));
        Assert.False(PRBadge.ShowsCreateBadge(false, true, pullRequest: null, defaultBranchAheadCommits: 0));
        Assert.False(PRBadge.ShowsCreateBadge(true, true, pullRequest: null, defaultBranchAheadCommits: 4));
    }

    [Fact]
    public void CreatePrs_Feature_UsesAheadOfParent_NotStaleHeadVsBase()
    {
        // Ahead of parent (Feature divergence) is enough even if HeadCommit is missing/stale.
        Assert.True(PRBadge.ShowsCreateBadge(
            false, true, pullRequest: null, defaultBranchAheadCommits: 1,
            headCommit: null, featureBaseCommitSha: "abc"));
        Assert.True(PRBadge.ShowsCreateBadge(
            false, true, pullRequest: null, defaultBranchAheadCommits: 1,
            headCommit: "abc", featureBaseCommitSha: "abc"));
        // Zero ahead and HEAD still at Feature base tip -> no create.
        Assert.False(PRBadge.ShowsCreateBadge(
            false, true, pullRequest: null, defaultBranchAheadCommits: 0,
            headCommit: "abc", featureBaseCommitSha: "abc"));
        // Zero ahead but HEAD moved since create (counts not refreshed yet) -> create.
        Assert.True(PRBadge.ShowsCreateBadge(
            false, true, pullRequest: null, defaultBranchAheadCommits: 0,
            headCommit: "def", featureBaseCommitSha: "abc"));
        // Workspace-style: no Feature base SHA, ahead of default.
        Assert.True(PRBadge.ShowsCreateBadge(
            false, true, pullRequest: null, defaultBranchAheadCommits: 2));
        Assert.False(PRBadge.ShowsCreateBadge(
            false, true, pullRequest: null, defaultBranchAheadCommits: 0));
    }

    [Fact]
    public void UncommittedChanges_TrueWhenCountIsAtLeastOne()
    {
        Assert.True(PRBadge.ShowsUncommittedChangesBadge(1));
        Assert.True(PRBadge.ShowsUncommittedChangesBadge(23));
    }

    [Fact]
    public void UncommittedChanges_FalseWhenCountIsZero()
    {
        Assert.False(PRBadge.ShowsUncommittedChangesBadge(0));
    }

    private static PullRequestInfo OpenPr() => new() { Number = 1, State = "open" };

    private static PullRequestInfo MergedPr() => new()
    {
        Number = 2,
        State = "closed",
        MergedAt = DateTimeOffset.UtcNow
    };

    private static PullRequestInfo ClosedPr() => new() { Number = 3, State = "closed" };
}
