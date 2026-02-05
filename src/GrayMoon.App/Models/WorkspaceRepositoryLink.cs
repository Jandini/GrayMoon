using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace GrayMoon.App.Models;

[Table("WorkspaceRepositories")]
public class WorkspaceRepositoryLink
{
    /// <summary>Primary key (column renamed from WorkspaceRepositoryLinkId).</summary>
    public int RepositoryId { get; set; }

    [Required]
    public int WorkspaceId { get; set; }

    [ForeignKey(nameof(WorkspaceId))]
    public Workspace? Workspace { get; set; }

    [Required]
    public int LinkedRepositoryId { get; set; }

    [ForeignKey(nameof(LinkedRepositoryId))]
    public Repository? Repository { get; set; }

    [MaxLength(100)]
    public string? GitVersion { get; set; }

    [MaxLength(200)]
    public string? BranchName { get; set; }

    /// <summary>Number of .csproj projects in the repository (excludes .git). Set during sync.</summary>
    public int? Projects { get; set; }

    /// <summary>Persisted sync status. New links default to <see cref="RepoSyncStatus.NeedsSync"/>.</summary>
    public RepoSyncStatus SyncStatus { get; set; } = RepoSyncStatus.NeedsSync;
}
