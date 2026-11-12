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

    /// <summary>
    /// Tag the special Workspace had checked out when this Feature repository was created. When set, the
    /// worktree is detached at that tag and has no Feature branch - never create, push or delete one here.
    /// </summary>
    [MaxLength(200)]
    public string? PinnedTag { get; set; }

    /// <summary>True when the Feature pins this repository to a tag: its worktree is detached and it has no Feature branch.</summary>
    public bool IsDetached => PinnedTag != null;

    /// <summary>
    /// True when this Feature holds the repository as a reference snapshot: the Source was a reference repository
    /// when the Feature was created, so the worktree is detached at <see cref="BaseCommitSha"/> and has no Feature
    /// branch. Set at creation and never changed afterwards.
    /// </summary>
    public bool IsReferenceSnapshot { get; set; }

    /// <summary>Branch the Source was on when the reference snapshot was taken; null when it was on a tag or this is not a snapshot.</summary>
    [MaxLength(200)]
    public string? SourceBranchName { get; set; }

    public DateTime CreatedAt { get; set; }

    public WorkspaceFeatureRepositoryState State { get; set; } = WorkspaceFeatureRepositoryState.Pending;

    [MaxLength(2000)]
    public string? LastError { get; set; }
}
