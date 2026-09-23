using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace GrayMoon.App.Models;

[Table("WorkspaceFeatureRepositories")]
public sealed class WorkspaceFeatureRepository
{
    public int WorkspaceFeatureRepositoryId { get; set; }

    [Required]
    public int WorkspaceFeatureContextId { get; set; }

    [ForeignKey(nameof(WorkspaceFeatureContextId))]
    public WorkspaceFeatureContext? WorkspaceFeatureContext { get; set; }

    [Required]
    public int WorkspaceRepositoryId { get; set; }

    [ForeignKey(nameof(WorkspaceRepositoryId))]
    public WorkspaceRepositoryLink? WorkspaceRepository { get; set; }

    [Required]
    [MaxLength(2000)]
    public string WorktreePath { get; set; } = string.Empty;

    [Required]
    [MaxLength(64)]
    public string BaseCommitSha { get; set; } = string.Empty;

    /// <summary>
    /// Workspace branch that was checked out when this Feature repository was created.
    /// Provenance / PR-target suggestion only - never used to restore the Workspace on Feature removal.
    /// </summary>
    [MaxLength(200)]
    public string? ParentBranchName { get; set; }

    public DateTime CreatedAt { get; set; }

    public WorkspaceFeatureRepositoryState State { get; set; } = WorkspaceFeatureRepositoryState.Pending;

    [MaxLength(2000)]
    public string? LastError { get; set; }
}
