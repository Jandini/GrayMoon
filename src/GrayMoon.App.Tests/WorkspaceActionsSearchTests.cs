using GrayMoon.App.Components.Modals;
using GrayMoon.App.Components.Pages;
using GrayMoon.App.Models;

namespace GrayMoon.App.Tests;

public sealed class WorkspaceActionsSearchTests
{
    [Fact]
    public void BuildChecksActionsUrl_AppendsRepoSearchQuery()
    {
        var url = MergePullRequestModal.BuildChecksActionsUrl("/workspaces/3/actions", "GrayMoon.App");

        Assert.Equal("/workspaces/3/actions?q=repo%3AGrayMoon.App", url);
    }

    [Fact]
    public void BuildChecksActionsUrl_LeavesUrlUnchangedWhenRepoNameMissing()
    {
        Assert.Equal("/workspaces/3/actions", MergePullRequestModal.BuildChecksActionsUrl("/workspaces/3/actions", null));
        Assert.Equal("/workspaces/3/actions", MergePullRequestModal.BuildChecksActionsUrl("/workspaces/3/actions", "  "));
        Assert.Equal(string.Empty, MergePullRequestModal.BuildChecksActionsUrl(null, "GrayMoon"));
    }

    [Fact]
    public void RepoFieldQuery_MatchesOnlyThatRepository()
    {
        var widgets = CreateRow("widgets");
        var api = CreateRow("graymoon-api");
        var line = CreateLine("CI");

        Assert.True(WorkspaceActions.MatchesSearch(widgets, line, "repo:widgets"));
        Assert.False(WorkspaceActions.MatchesSearch(api, line, "repo:widgets"));
    }

    [Fact]
    public void RepoFieldQuery_DoesNotMatchWorkflowNameOnAnotherRepo()
    {
        var other = CreateRow("unrelated");
        var line = CreateLine("widgets-build");

        Assert.False(WorkspaceActions.MatchesSearch(other, line, "repo:widgets"));
        Assert.True(WorkspaceActions.MatchesSearch(other, line, "widgets"));
    }

    private static WorkspaceActions.WorkspaceActionRow CreateRow(string repositoryName) => new()
    {
        Link = new WorkspaceRepositoryLink { BranchName = "main" },
        Repo = new GitHubRepositoryEntry { RepositoryName = repositoryName, OrgName = "acme", ConnectorName = "GitHub" }
    };

    private static WorkspaceActions.WorkflowActionLine CreateLine(string workflowName) => new()
    {
        Action = new ActionStatusInfo
        {
            Status = "running",
            BranchName = "main",
            WorkflowName = workflowName
        }
    };
}
