using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace GrayMoon.App.Models;

/// <summary>Dependency edge: DependentProject references ReferencedProject (e.g. via NuGet PackageReference). Only workspace-internal dependencies are persisted.</summary>
[Table("ProjectDependencies")]
public class ProjectDependency
{
    public int ProjectDependencyId { get; set; }

    /// <summary>Project that has the package reference (depends on).</summary>
    [Required]
    public int DependentProjectId { get; set; }

    [ForeignKey(nameof(DependentProjectId))]
    public RepositoryProject? DependentProject { get; set; }

    /// <summary>Workspace project that produces the package (referenced by).</summary>
    [Required]
    public int ReferencedProjectId { get; set; }

    [ForeignKey(nameof(ReferencedProjectId))]
    public RepositoryProject? ReferencedProject { get; set; }

    /// <summary>Package version from the .csproj PackageReference (e.g. 1.2.3 or 1.0.0-*).</summary>
    [MaxLength(100)]
    public string? Version { get; set; }
}
