using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace GrayMoon.App.Models;

[Table("WorkspaceRepositoryContextActions")]
public sealed class WorkspaceRepositoryContextAction
{
    public int WorkspaceRepositoryContextActionId { get; set; }

    [Required]
    public int WorkspaceFeatureContextId { get; set; }

    [ForeignKey(nameof(WorkspaceFeatureContextId))]
    public WorkspaceFeatureContext? WorkspaceFeatureContext { get; set; }

    [Required]
    public int WorkspaceRepositoryId { get; set; }

    [ForeignKey(nameof(WorkspaceRepositoryId))]
    public WorkspaceRepositoryLink? WorkspaceRepository { get; set; }

    public string? Status { get; set; }
    public string? HtmlUrl { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
    public string? BranchName { get; set; }
    public long? RunId { get; set; }
    public long? WorkflowId { get; set; }
    public string? WorkflowName { get; set; }
    public string? WorkflowsJson { get; set; }
    public DateTime LastCheckedAt { get; set; }

    public ActionStatusInfo ToActionStatusInfo() => new()
    {
        Status = Status ?? "none",
        HtmlUrl = HtmlUrl,
        UpdatedAt = UpdatedAt,
        BranchName = BranchName,
        RunId = RunId,
        WorkflowId = WorkflowId,
        WorkflowName = WorkflowName,
        SupportsWorkflowDispatch = false
    };
}
