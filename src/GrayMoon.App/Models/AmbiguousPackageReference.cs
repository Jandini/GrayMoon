using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace GrayMoon.App.Models;

/// <summary>
/// A PackageReference whose package id is published by more than one workspace project. Kept so Packages can list
/// the referencing repositories without adding a <see cref="ProjectDependency"/> edge, which would pick a producer
/// for the dependency graph and for push order.
/// </summary>
[Table("AmbiguousPackageReferences")]
public class AmbiguousPackageReference
{
    public int AmbiguousPackageReferenceId { get; set; }

    /// <summary>Project that has the package reference.</summary>
    [Required]
    public int DependentProjectId { get; set; }

    [ForeignKey(nameof(DependentProjectId))]
    public WorkspaceProject? DependentProject { get; set; }

    /// <summary>Package id as declared on the PackageReference. Matched to producers by id, ignoring case.</summary>
    [Required]
    [MaxLength(200)]
    public string PackageId { get; set; } = string.Empty;

    /// <summary>Version from the PackageReference, when one was declared.</summary>
    [MaxLength(100)]
    public string? Version { get; set; }
}
