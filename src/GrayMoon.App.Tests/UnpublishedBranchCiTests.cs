using GrayMoon.App.Components.Pages;
using GrayMoon.App.Models;
using GrayMoon.App.Services.Policy;
using GrayMoon.App.Services.Queries;

namespace GrayMoon.App.Tests;

/// <summary>
/// A branch with no upstream and no commits (the gray cloud badge): the Actions page shows the runs of its parent branch,
/// and clicking the badge asks before it sets the upstream.
/// </summary>
public sealed class UnpublishedBranchCiTests
{
    [Theory]
    [InlineData(false, 0, "main", "main")]
    [InlineData(false, null, "main", "main")]
    [InlineData(false, 0, "origin/develop", "develop")]
    [InlineData(false, 2, "main", "feature/x")]
    [InlineData(true, 0, "main", "feature/x")]
    [InlineData(null, 0, "main", "feature/x")]
    [InlineData(false, 0, null, "feature/x")]
    [InlineData(false, 0, " ", "feature/x")]
    public void Ci_branch_is_the_parent_only_while_the_branch_has_no_upstream_and_no_commits(
        bool? hasUpstream, int? outgoing, string? parent, string expected)
    {
        Assert.Equal(expected, UnpublishedBranchRules.CiBranch("feature/x", hasUpstream, outgoing, parent));
    }

    [Fact]
    public void Actions_row_of_an_unpublished_branch_without_commits_reads_the_recorded_parent_branch()
    {
        var link = new WorkspaceRepositoryLink { BranchName = "feature/x" };
        var state = Snapshot(branch: "feature/x", hasUpstream: false, outgoing: 0, baselineParent: "main");

        var ciBranch = WorkspaceActions.ResolveCiBranch(link, state);
        var row = Row(link, ciBranch);

        Assert.Equal("main", ciBranch);
        Assert.True(row.ShowsParentBranch);
        Assert.Contains("parent branch main", WorkspaceActions.ParentBranchTitle(row));

        // A failed run on the parent branch counts and can be run again on that branch.
        var line = new WorkspaceActions.WorkflowActionLine
        {
            Action = new ActionStatusInfo { Status = "failed", BranchName = "main", RunId = 1, WorkflowId = 10, SupportsWorkflowDispatch = true }
        };
        Assert.True(WorkspaceActions.CanRunAgain(row, line));
    }

    [Fact]
    public void Actions_row_switches_back_to_its_own_branch_once_it_has_a_commit_or_an_upstream()
    {
        var link = new WorkspaceRepositoryLink { BranchName = "feature/x" };

        Assert.Equal("feature/x", WorkspaceActions.ResolveCiBranch(link, Snapshot("feature/x", hasUpstream: false, outgoing: 1, baselineParent: "main")));
        Assert.Equal("feature/x", WorkspaceActions.ResolveCiBranch(link, Snapshot("feature/x", hasUpstream: true, outgoing: 0, baselineParent: "main")));
        Assert.False(Row(link, "feature/x").ShowsParentBranch);
    }

    [Fact]
    public void Actions_row_without_a_recorded_parent_stays_on_its_own_branch()
    {
        var link = new WorkspaceRepositoryLink { BranchName = "feature/x" };

        Assert.Equal("feature/x", WorkspaceActions.ResolveCiBranch(link, Snapshot("feature/x", hasUpstream: false, outgoing: 0, baselineParent: null)));
        Assert.Equal("feature/x", WorkspaceActions.ResolveCiBranch(link, contextState: null));
    }

    [Theory]
    [InlineData(false, 0, true)]
    [InlineData(false, null, true)]
    [InlineData(false, 3, false)]
    [InlineData(true, 0, false)]
    [InlineData(null, 0, false)]
    public void Gray_cloud_badge_is_a_branch_without_upstream_and_commits(bool? hasUpstream, int? outgoing, bool expected)
    {
        Assert.Equal(expected, UnpublishedBranchRules.HasNoUpstreamAndNoCommits(hasUpstream, outgoing));
    }

    [Fact]
    public void Gray_cloud_badge_click_asks_to_confirm_that_no_commits_were_made()
    {
        Assert.Equal(
            "No commits were made on feature/x in widgets. Push the branch anyway to set its upstream?",
            WorkspaceRepositories.UpstreamOnlyPushMessage("widgets", "feature/x"));
        Assert.Equal(
            "No commits were made in widgets. Push the branch anyway to set its upstream?",
            WorkspaceRepositories.UpstreamOnlyPushMessage("widgets", null));
    }

    private static WorkspaceActions.WorkspaceActionRow Row(WorkspaceRepositoryLink link, string? ciBranch) => new()
    {
        Link = link,
        CiBranchName = ciBranch,
        Repo = new GitHubRepositoryEntry { RepositoryName = "widgets", OrgName = "acme", ConnectorName = "GitHub" }
    };

    private static WorkspaceRepositoryLinkListItemDto Snapshot(string branch, bool? hasUpstream, int? outgoing, string? baselineParent)
        => new(
            WorkspaceRepositoryId: 1, WorkspaceId: 1, RepositoryId: 1, RepositoryName: "widgets", CloneUrl: "https://x/widgets",
            GitVersion: null, BranchName: branch, CheckedOutTag: null, DefaultBranchName: "main",
            OutgoingCommits: outgoing, IncomingCommits: 0, DefaultBranchBehindCommits: 0, DefaultBranchAheadCommits: 0, BranchHasUpstream: hasUpstream,
            SyncStatus: RepoSyncStatus.InSync, DependencyLevel: 1, Dependencies: 0, UnmatchedDeps: 0, OutOfDateFileRepos: 0,
            RepositoryType: null, HasNewerTag: null, HasSelfFileVersionToken: null, PullRequestState: null, PullRequestNumber: null,
            PullRequestHtmlUrl: null, PullRequestMergedAt: null, PullRequestMergeable: null, PullRequestMergeableState: null,
            PullRequestChangedFiles: null, Archived: false, UncommittedChangedFileCount: 0,
            BaselineParentBranchName: baselineParent);
}
