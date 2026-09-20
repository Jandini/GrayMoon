using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace GrayMoon.App.Models;

[Table("WorkspaceFeatures")]
public sealed class WorkspaceFeature
{
    public int WorkspaceFeatureId { get; set; }

    [Required]
    public int WorkspaceId { get; set; }

    [ForeignKey(nameof(WorkspaceId))]
    public Workspace? Workspace { get; set; }

    [Required]
    [MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    public WorkspaceFeatureLifecycleState LifecycleState { get; set; } = WorkspaceFeatureLifecycleState.Creating;

    public WorkspaceFeatureBaseKind BaseKind { get; set; } = WorkspaceFeatureBaseKind.CurrentWorkspace;

    public int? BaseWorkspaceFeatureId { get; set; }

    [ForeignKey(nameof(BaseWorkspaceFeatureId))]
    public WorkspaceFeature? BaseWorkspaceFeature { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    [MaxLength(2000)]
    public string? LastError { get; set; }

    public WorkspaceFeatureContext? Context { get; set; }
}
