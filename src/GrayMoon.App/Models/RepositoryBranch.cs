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

    /// <summary>True if this is the repository's default branch (e.g. main or master). Set when branches are refreshed from agent.</summary>
    public bool IsDefault { get; set; }

    /// <summary>True if this row represents a Git tag rather than a branch. Tag rows always have <see cref="IsRemote"/> = false.</summary>
    public bool IsTag { get; set; }

    /// <summary>Rank within the fetched list as reported by the agent (0 = first/newest). Only meaningful for tags, which the agent returns ordered by creator date descending; used to preserve "newest first" order across reads. Branches do not use this and default to 0.</summary>
    public int SortIndex { get; set; }
}
