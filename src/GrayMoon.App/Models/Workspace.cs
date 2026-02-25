using System.ComponentModel.DataAnnotations;

namespace GrayMoon.App.Models;

public class Workspace
{
    public int WorkspaceId { get; set; }

    [Required]
    [MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    public bool IsDefault { get; set; }

    [MaxLength(500)]
    public string? RootPath { get; set; }

    public DateTime? LastSyncedAt { get; set; }

    public bool IsInSync { get; set; }

    public ICollection<WorkspaceRepositoryLink> Repositories { get; set; } = new List<WorkspaceRepositoryLink>();
}
