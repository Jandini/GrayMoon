using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace GrayMoon.App.Models;

[Table("WorkspaceFileContextStates")]
public sealed class WorkspaceFileContextState
{
    public int WorkspaceFileContextStateId { get; set; }

    [Required]
    public int WorkspaceFeatureContextId { get; set; }

    [ForeignKey(nameof(WorkspaceFeatureContextId))]
    public WorkspaceFeatureContext? WorkspaceFeatureContext { get; set; }

    [Required]
    public int FileId { get; set; }

    [ForeignKey(nameof(FileId))]
    public WorkspaceFile? File { get; set; }

    public bool? IsMissingOnDisk { get; set; }
    public DateTime? LastCheckedAt { get; set; }
}
