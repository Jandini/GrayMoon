using System.ComponentModel.DataAnnotations;

namespace GrayMoon.App.Models;

public class Workspace
{
    public int WorkspaceId { get; set; }

    [Required]
    [MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    public ICollection<WorkspaceRepositoryLink> Repositories { get; set; } = new List<WorkspaceRepositoryLink>();
}
