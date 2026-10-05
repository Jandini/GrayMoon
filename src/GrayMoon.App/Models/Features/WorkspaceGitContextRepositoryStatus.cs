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
    public DateTimeOffset AgentScannedAt { get; set; }
    public DateTimeOffset PersistedAt { get; set; }
    public string? LastErrorCode { get; set; }
    public string? LastErrorMessage { get; set; }
}
