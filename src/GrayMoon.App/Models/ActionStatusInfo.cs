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
}
