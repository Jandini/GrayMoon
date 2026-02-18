using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace GrayMoon.App.Models;

[Table("RepositoryBranches")]
public class RepositoryBranch
{
    /// <summary>Primary key.</summary>
    public int RepositoryBranchId { get; set; }

    [Required]
    public int WorkspaceRepositoryId { get; set; }

    [ForeignKey(nameof(WorkspaceRepositoryId))]
    public WorkspaceRepositoryLink? WorkspaceRepository { get; set; }

    [Required]
    [MaxLength(200)]
    public string BranchName { get; set; } = string.Empty;

    /// <summary>True if this is a remote branch (from origin), false if local.</summary>
    [Required]
    public bool IsRemote { get; set; }

    /// <summary>Timestamp when this branch was last seen/fetched.</summary>
    [Required]
    public DateTime LastSeenAt { get; set; } = DateTime.UtcNow;
}
