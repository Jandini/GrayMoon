using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace GrayMoon.App.Models;

/// <summary>
/// Per-workspace navigation preference for which Feature context to show on next open.
/// Never used as execution authority - operations receive an explicit context id.
/// </summary>
[Table("WorkspaceSelectedFeatureContexts")]
public sealed class WorkspaceSelectedFeatureContext
{
    [Key]
    public int WorkspaceId { get; set; }

    [ForeignKey(nameof(WorkspaceId))]
    public Workspace? Workspace { get; set; }

    public int WorkspaceFeatureContextId { get; set; }

    [ForeignKey(nameof(WorkspaceFeatureContextId))]
    public WorkspaceFeatureContext? WorkspaceFeatureContext { get; set; }

    public DateTime UpdatedAt { get; set; }
}
