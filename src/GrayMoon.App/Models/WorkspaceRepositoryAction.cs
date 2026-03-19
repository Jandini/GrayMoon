using System.ComponentModel.DataAnnotations.Schema;

namespace GrayMoon.App.Models;

[Table("WorkspaceRepositoryActions")]
public class WorkspaceRepositoryAction
{
    public int WorkspaceRepositoryId { get; set; }

    [ForeignKey(nameof(WorkspaceRepositoryId))]
    public WorkspaceRepositoryLink? WorkspaceRepository { get; set; }

    /// <summary>Aggregate status: "none", "success", "running", "failed".</summary>
    public string? Status { get; set; }

    public string? HtmlUrl { get; set; }

    public DateTimeOffset? UpdatedAt { get; set; }

    /// <summary>The branch this status was fetched for.</summary>
    public string? BranchName { get; set; }

    public long? RunId { get; set; }

    public long? WorkflowId { get; set; }

    public string? WorkflowName { get; set; }

    public DateTime LastCheckedAt { get; set; }

    public ActionStatusInfo ToActionStatusInfo() => new()
    {
        Status = Status ?? "none",
        HtmlUrl = HtmlUrl,
        UpdatedAt = UpdatedAt,
        BranchName = BranchName,
        RunId = RunId,
        WorkflowId = WorkflowId,
        WorkflowName = WorkflowName
    };
}
