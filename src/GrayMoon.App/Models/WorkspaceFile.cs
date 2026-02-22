using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace GrayMoon.App.Models;

/// <summary>User-selected file within a workspace repository. Path is relative to repository root.</summary>
[Table("WorkspaceFiles")]
public class WorkspaceFile
{
    public int FileId { get; set; }

    [Required]
    public int WorkspaceId { get; set; }

    [ForeignKey(nameof(WorkspaceId))]
    public Workspace? Workspace { get; set; }

    [Required]
    public int RepositoryId { get; set; }

    [ForeignKey(nameof(RepositoryId))]
    public Repository? Repository { get; set; }

    [Required]
    [MaxLength(260)]
    public string FileName { get; set; } = string.Empty;

    /// <summary>Path relative to the repository root.</summary>
    [Required]
    [MaxLength(2000)]
    public string FilePath { get; set; } = string.Empty;
}
