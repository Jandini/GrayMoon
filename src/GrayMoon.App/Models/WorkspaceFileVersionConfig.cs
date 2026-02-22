using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace GrayMoon.App.Models;

/// <summary>Version update pattern configuration for a workspace file.</summary>
[Table("WorkspaceFileVersionConfigs")]
public class WorkspaceFileVersionConfig
{
    public int ConfigId { get; set; }

    [Required]
    public int FileId { get; set; }

    [ForeignKey(nameof(FileId))]
    public WorkspaceFile? File { get; set; }

    /// <summary>
    /// Multi-line pattern text. Each line is KEY={repositoryname}.
    /// Used to match lines in the file and substitute the resolved version.
    /// </summary>
    [Required]
    public string VersionPattern { get; set; } = string.Empty;
}
