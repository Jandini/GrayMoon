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

    public DateTime CreatedAt { get; set; }

    public WorkspaceFeatureRepositoryState State { get; set; } = WorkspaceFeatureRepositoryState.Pending;

    [MaxLength(2000)]
    public string? LastError { get; set; }
}
