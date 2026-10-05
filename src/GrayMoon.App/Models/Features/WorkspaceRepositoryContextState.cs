using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace GrayMoon.App.Models;

[Table("WorkspaceRepositoryContextStates")]
public sealed class WorkspaceRepositoryContextState
{
    public int WorkspaceRepositoryContextStateId { get; set; }

    [Required]
    public int WorkspaceFeatureContextId { get; set; }

    [ForeignKey(nameof(WorkspaceFeatureContextId))]
    public WorkspaceFeatureContext? WorkspaceFeatureContext { get; set; }

    [Required]
    public int WorkspaceRepositoryId { get; set; }

    [ForeignKey(nameof(WorkspaceRepositoryId))]
    public WorkspaceRepositoryLink? WorkspaceRepository { get; set; }

    [MaxLength(200)]
    public string? BranchName { get; set; }

    [MaxLength(200)]
    public string? CheckedOutTag { get; set; }

    [MaxLength(64)]
    public string? HeadCommit { get; set; }

    public bool? HasNewerTag { get; set; }

    [MaxLength(100)]
    public string? GitVersion { get; set; }

    public int? Projects { get; set; }
    public int? OutgoingCommits { get; set; }
    public int? IncomingCommits { get; set; }
    public int? DefaultBranchBehindCommits { get; set; }
    public int? DefaultBranchAheadCommits { get; set; }
    public bool? BranchHasUpstream { get; set; }

    public RepoSyncStatus SyncStatus { get; set; } = RepoSyncStatus.NeedsSync;

    public int? DependencyLevel { get; set; }
    public int? Dependencies { get; set; }
    public int? UnmatchedDeps { get; set; }
    public int? OutOfDateFileLines { get; set; }
    public int? OutOfDateFileRepos { get; set; }
    public int? TotalFileConfigRepos { get; set; }
    public bool? HasSelfFileVersionToken { get; set; }
    public int? TotalFileLines { get; set; }
    public ProjectType? RepositoryType { get; set; }
}
