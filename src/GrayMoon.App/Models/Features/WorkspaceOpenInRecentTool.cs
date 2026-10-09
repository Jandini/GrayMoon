using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace GrayMoon.App.Models;

/// <summary>
/// One Open-in tool remembered on the feature-selector bar for a Workspace.
/// <see cref="Position"/> is the left-to-right order. A tool stays in its slot until removed.
/// </summary>
[Table("WorkspaceOpenInRecentTools")]
public sealed class WorkspaceOpenInRecentTool
{
    public int WorkspaceId { get; set; }

    [ForeignKey(nameof(WorkspaceId))]
    public Workspace? Workspace { get; set; }

    [MaxLength(32)]
    public string ToolId { get; set; } = string.Empty;

    public int Position { get; set; }
}
