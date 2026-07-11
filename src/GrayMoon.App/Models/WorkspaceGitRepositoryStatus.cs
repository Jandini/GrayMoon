using System.ComponentModel.DataAnnotations.Schema;

namespace GrayMoon.App.Models;

/// <summary>
/// Persisted read model for one repository's latest Git Changes status - the App's SQLite projection of
/// an Agent-reported <see cref="GrayMoon.Common.Git.GitChangeSnapshot"/>. Git and the Agent remain
/// authoritative; this is a durable cache the Git Changes page reads without ever contacting the Agent.
/// </summary>
[Table("WorkspaceGitRepositoryStatus")]
public sealed class WorkspaceGitRepositoryStatus
{
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

    public DateTimeOffset AgentScannedAt { get; set; }
    public DateTimeOffset PersistedAt { get; set; }

    public string? LastErrorCode { get; set; }
    public string? LastErrorMessage { get; set; }
}
