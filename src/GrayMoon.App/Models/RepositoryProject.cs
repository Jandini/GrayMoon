using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace GrayMoon.App.Models;

/// <summary>Persisted .csproj project within a repository. Merge key: RepositoryId + ProjectName.</summary>
[Table("RepositoryProjects")]
public class RepositoryProject
{
    public int ProjectId { get; set; }

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
}
