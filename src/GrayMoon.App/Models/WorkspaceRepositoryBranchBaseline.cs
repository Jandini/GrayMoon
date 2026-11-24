using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace GrayMoon.App.Models;

/// <summary>
/// Where a branch of the special Workspace started, recorded when GrayMoon created it (Prepare Workspace, Create
/// Branches, New Branch, REST). One row per (context, repository, branch). A Feature does not use this table: its
/// <see cref="WorkspaceFeatureRepository"/> row is its baseline. Written once at creation and never updated; a branch
/// created again under the same name replaces the row, and deleting the local branch through GrayMoon deletes it.
/// </summary>
[Table("WorkspaceRepositoryBranchBaselines")]
public sealed class WorkspaceRepositoryBranchBaseline
{
    public int WorkspaceRepositoryBranchBaselineId { get; set; }

    [Required]
    public int WorkspaceFeatureContextId { get; set; }

    [ForeignKey(nameof(WorkspaceFeatureContextId))]
    public WorkspaceFeatureContext? WorkspaceFeatureContext { get; set; }

    [Required]
    public int WorkspaceRepositoryId { get; set; }

    [ForeignKey(nameof(WorkspaceRepositoryId))]
    public WorkspaceRepositoryLink? WorkspaceRepository { get; set; }

    [Required]
    [MaxLength(200)]
    public string BranchName { get; set; } = string.Empty;

    /// <summary>The base as requested, without <c>origin/</c>.</summary>
    [Required]
    [MaxLength(200)]
    public string ParentBranchName { get; set; } = string.Empty;

    [Required]
    [MaxLength(64)]
    public string BaseCommitSha { get; set; } = string.Empty;

    /// <summary>The ref git created the branch from (<c>origin/release/2.4</c> or <c>release/2.4</c>). Diagnostic only.</summary>
    [MaxLength(250)]
    public string? ResolvedStartRef { get; set; }

    public DateTime CreatedAtUtc { get; set; }
}
