using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace GrayMoon.App.Models;

/// <summary>Persisted .csproj project within a repository for a specific workspace. Merge key: WorkspaceId + RepositoryId + ProjectName.</summary>
[Table("WorkspaceProjects")]
public class WorkspaceProject
{
    public int ProjectId { get; set; }

    [Required]
    public int WorkspaceId { get; set; }

    [ForeignKey(nameof(WorkspaceId))]
    public Workspace? Workspace { get; set; }

    [Required]
    public int RepositoryId { get; set; }

    [ForeignKey(nameof(RepositoryId))]
    public Repository? Repository { get; set; }

    [Required]
    [MaxLength(200)]
    public string ProjectName { get; set; } = string.Empty;

    public ProjectType ProjectType { get; set; }

    [Required]
    [MaxLength(500)]
    public string ProjectFilePath { get; set; } = string.Empty;

    [Required]
    [MaxLength(100)]
    public string TargetFramework { get; set; } = string.Empty;

    [MaxLength(200)]
    public string? PackageId { get; set; }

    /// <summary>Outgoing dependencies (projects this one references).</summary>
    [InverseProperty(nameof(ProjectDependency.DependentProject))]
    public ICollection<ProjectDependency> DependsOn { get; set; } = new List<ProjectDependency>();

    /// <summary>Incoming dependencies (projects that reference this one).</summary>
    [InverseProperty(nameof(ProjectDependency.ReferencedProject))]
    public ICollection<ProjectDependency> ReferencedBy { get; set; } = new List<ProjectDependency>();
}
