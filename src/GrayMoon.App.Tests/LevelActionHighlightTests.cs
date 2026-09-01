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
