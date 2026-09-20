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

    public bool ExcludeAiWorkflows { get; set; } = true;

    /// <summary>Persisted GrayMoon-managed Feature storage root (e.g. C:\Workspace\.graymoon\AVR\features). Set on first Feature creation; not relocated on Workspace rename.</summary>
    [MaxLength(1000)]
    public string? ManagedFeatureStorageRoot { get; set; }

    public ICollection<WorkspaceRepositoryLink> Repositories { get; set; } = new List<WorkspaceRepositoryLink>();
}
