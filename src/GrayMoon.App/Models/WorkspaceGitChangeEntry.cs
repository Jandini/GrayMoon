using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using GrayMoon.Common.Git;

namespace GrayMoon.App.Models;

/// <summary>One changed path from the most recent persisted Git Changes snapshot for a repository. Cleared
/// and rebuilt in full on every snapshot update - never partially patched.</summary>
[Table("WorkspaceGitChangeEntries")]
public sealed class WorkspaceGitChangeEntry
{
    public int WorkspaceGitChangeEntryId { get; set; }

    public int WorkspaceRepositoryId { get; set; }

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
