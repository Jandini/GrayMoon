using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace GrayMoon.App.Models;

[Table("WorkspaceGitContextRepositoryStatuses")]
public sealed class WorkspaceGitContextRepositoryStatus
{
    public int WorkspaceGitContextRepositoryStatusId { get; set; }

    [Required]
    public int WorkspaceFeatureContextId { get; set; }

    [ForeignKey(nameof(WorkspaceFeatureContextId))]
    public WorkspaceFeatureContext? WorkspaceFeatureContext { get; set; }

    [Required]
    public int WorkspaceRepositoryId { get; set; }

    [ForeignKey(nameof(WorkspaceRepositoryId))]
    public WorkspaceRepositoryLink? WorkspaceRepository { get; set; }

    public long SnapshotVersion { get; set; }
    public string? BranchName { get; set; }
    public string? HeadCommit { get; set; }
    public bool IsDetachedHead { get; set; }
    public bool IsUnbornBranch { get; set; }
    public bool IsMerging { get; set; }
    public bool IsRebasing { get; set; }
    public bool IsCherryPicking { get; set; }
    public int StagedCount { get; set; }
    public int ChangedCount { get; set; }
    public int ConflictCount { get; set; }
    public int? Insertions { get; set; }
    public int? Deletions { get; set; }
    public int? StagedInsertions { get; set; }
    public int? StagedDeletions { get; set; }
    public DateTimeOffset WorkerScannedAt { get; set; }
    public DateTimeOffset PersistedAt { get; set; }
    /// <summary>
    /// Hash of the persisted snapshot content (every entry plus the status fields the page shows). Null for rows
    /// written before the digest existed. Equal digests mean a live update changes nothing visible.
    /// </summary>
    [MaxLength(64)]
    public string? ContentDigest { get; set; }

    /// <summary>Distinct path rows persisted for this snapshot. Null for rows written before the digest existed.</summary>
    public int? PathCount { get; set; }

    public string? LastErrorCode { get; set; }
    public string? LastErrorMessage { get; set; }
}
