using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using GrayMoon.Common.Git;

namespace GrayMoon.App.Models;

[Table("WorkspaceGitContextChangeEntries")]
public sealed class WorkspaceGitContextChangeEntry
{
    public int WorkspaceGitContextChangeEntryId { get; set; }

    [Required]
    public int WorkspaceFeatureContextId { get; set; }

    [ForeignKey(nameof(WorkspaceFeatureContextId))]
    public WorkspaceFeatureContext? WorkspaceFeatureContext { get; set; }

    [Required]
    public int WorkspaceRepositoryId { get; set; }

    [ForeignKey(nameof(WorkspaceRepositoryId))]
    public WorkspaceRepositoryLink? WorkspaceRepository { get; set; }

    [MaxLength(2000)]
    public string Path { get; set; } = "";

    [MaxLength(2000)]
    public string? OriginalPath { get; set; }

    public GitChangeKind IndexChange { get; set; }
    public GitChangeKind WorktreeChange { get; set; }
    public bool IsTracked { get; set; }
    public bool IsConflicted { get; set; }
    public bool IsSubmodule { get; set; }
}
