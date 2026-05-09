namespace GrayMoon.App.Models;

/// <summary>Display model for a repository's aggregated CI action status for a branch (from GitHub Actions).</summary>
public sealed class ActionStatusInfo
{
    /// <summary>Aggregate status: "none", "success", "running", "failed".</summary>
    public string Status { get; set; } = "none";

    public string? HtmlUrl { get; set; }

    public DateTimeOffset? UpdatedAt { get; set; }

    /// <summary>The branch this status was fetched for.</summary>
    public string? BranchName { get; set; }

    /// <summary>Run ID of the most relevant workflow run (used for re-run on failure).</summary>
    public long? RunId { get; set; }

    /// <summary>Workflow ID of the most relevant run (used for workflow_dispatch).</summary>
    public long? WorkflowId { get; set; }

    /// <summary>Display name of the most relevant workflow.</summary>
    public string? WorkflowName { get; set; }

    /// <summary>Browser URL for this workflow (Actions tab for the definition), not a specific run.</summary>
    public string? WorkflowHtmlUrl { get; set; }

    /// <summary>Repo-relative workflow file path (e.g. <c>.github/workflows/build-app.yml</c>) for building the Actions workflow tab URL.</summary>
    public string? WorkflowPath { get; set; }

    /// <summary>True when the workflow file declares a <c>workflow_dispatch</c> trigger (manual run is allowed).</summary>
    public bool SupportsWorkflowDispatch { get; set; }
}
