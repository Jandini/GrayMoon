using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace GrayMoon.App.Models;

[Table("WorkspaceFeatureContexts")]
public sealed class WorkspaceFeatureContext
{
    public int WorkspaceFeatureContextId { get; set; }

    [Required]
    public int WorkspaceId { get; set; }

    [ForeignKey(nameof(WorkspaceId))]
    public Workspace? Workspace { get; set; }

    public WorkspaceFeatureContextKind Kind { get; set; } = WorkspaceFeatureContextKind.Workspace;

    public int? WorkspaceFeatureId { get; set; }

    [ForeignKey(nameof(WorkspaceFeatureId))]
    public WorkspaceFeature? WorkspaceFeature { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? LastSyncedAt { get; set; }

    public bool IsInSync { get; set; }

    public ICollection<WorkspaceRepositoryContextState> RepositoryStates { get; set; } = new List<WorkspaceRepositoryContextState>();

    public ICollection<WorkspaceFeatureRepository> FeatureRepositories { get; set; } = new List<WorkspaceFeatureRepository>();
}
