using GrayMoon.App.Components.Pages;
using GrayMoon.App.Models;

namespace GrayMoon.App.Tests;

public sealed class WorkspaceActionsGatingTests
{
    private static WorkspaceActions.WorkspaceActionRow CreateRow(string? branchName) => new()
    {
        Link = new WorkspaceRepositoryLink { BranchName = branchName },
        Repo = new GitHubRepositoryEntry { RepositoryName = "widgets", OrgName = "acme", ConnectorName = "GitHub" }
    };

    private static WorkspaceActions.WorkflowActionLine CreateLine(
        string status,
        string? branchName,
        long? runId,
        long? workflowId,
        bool supportsWorkflowDispatch) => new()
    {
        Action = new ActionStatusInfo
        {
            Status = status,
            BranchName = branchName,
            RunId = runId,
            WorkflowId = workflowId,
            SupportsWorkflowDispatch = supportsWorkflowDispatch
        }
    };

    [Fact]
    public void CanRunAgain_TrueWhenFailedRunAlsoSupportsWorkflowDispatch()
    {
        var row = CreateRow("main");
        var line = CreateLine("failed", "main", runId: 1, workflowId: 10, supportsWorkflowDispatch: true);

        Assert.True(WorkspaceActions.CanRerun(row, line));
        Assert.True(WorkspaceActions.CanRun(row, line));
        Assert.True(WorkspaceActions.CanRunAgain(row, line));
    }

    [Fact]
    public void CanRunAgain_FalseWhenFailedRunDoesNotSupportWorkflowDispatch()
    {
        var row = CreateRow("main");
        var line = CreateLine("failed", "main", runId: 1, workflowId: 10, supportsWorkflowDispatch: false);

        Assert.True(WorkspaceActions.CanRerun(row, line));
        Assert.False(WorkspaceActions.CanRun(row, line));
        Assert.False(WorkspaceActions.CanRunAgain(row, line));
    }

    [Fact]
    public void CanRunAgain_FalseWhenRunHasNotFailed()
    {
        var row = CreateRow("main");
        var line = CreateLine("success", "main", runId: 1, workflowId: 10, supportsWorkflowDispatch: true);

        Assert.False(WorkspaceActions.CanRerun(row, line));
        Assert.False(WorkspaceActions.CanRunAgain(row, line));
    }

    [Fact]
    public void CanRunAgain_FalseWhenNoBranchIsSelected()
    {
        var row = CreateRow(null);
        var line = CreateLine("failed", null, runId: 1, workflowId: 10, supportsWorkflowDispatch: true);

        Assert.False(WorkspaceActions.CanRun(row, line));
        Assert.False(WorkspaceActions.CanRunAgain(row, line));
    }
}
