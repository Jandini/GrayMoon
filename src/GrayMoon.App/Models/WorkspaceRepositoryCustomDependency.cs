using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace GrayMoon.App.Models;

/// <summary>User-declared repo-level dependency edge within a workspace (ordering only, no version sync).</summary>
[Table("WorkspaceRepositoryCustomDependencies")]
public class WorkspaceRepositoryCustomDependency
{
    public int CustomDependencyId { get; set; }

    [Required]
    public int DependentWorkspaceRepositoryId { get; set; }

    [ForeignKey(nameof(DependentWorkspaceRepositoryId))]
    public WorkspaceRepositoryLink? DependentWorkspaceRepository { get; set; }

    [Required]
    public int ReferencedWorkspaceRepositoryId { get; set; }

    [ForeignKey(nameof(ReferencedWorkspaceRepositoryId))]
    public WorkspaceRepositoryLink? ReferencedWorkspaceRepository { get; set; }
}
