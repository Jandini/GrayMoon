using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace GrayMoon.App.Models;

[Table("WorkspaceRepositories")]
public class WorkspaceRepositoryLink
{
    /// <summary>Primary key for the workspace–repository link row.</summary>
    public int WorkspaceRepositoryId { get; set; }

    [Required]
    public int WorkspaceId { get; set; }

    [ForeignKey(nameof(WorkspaceId))]
    public Workspace? Workspace { get; set; }

    [Required]
    public int RepositoryId { get; set; }

    [ForeignKey(nameof(RepositoryId))]
    public Repository? Repository { get; set; }

    [MaxLength(100)]
    public string? GitVersion { get; set; }

    [MaxLength(200)]
    public string? BranchName { get; set; }

    /// <summary>Number of .csproj projects in the repository (excludes .git). Set during sync.</summary>
    public int? Projects { get; set; }

    /// <summary>Outgoing commits (ahead of remote). Set during sync after fetch.</summary>
    public int? OutgoingCommits { get; set; }

    /// <summary>Incoming commits (behind remote). Set during sync after fetch.</summary>
    public int? IncomingCommits { get; set; }

    /// <summary>Persisted sync status. New links default to <see cref="RepoSyncStatus.NeedsSync"/>.</summary>
    public RepoSyncStatus SyncStatus { get; set; } = RepoSyncStatus.NeedsSync;

    /// <summary>Build order sequence (same value = build in parallel). Set when dependencies are merged.</summary>
    public int? Sequence { get; set; }

    /// <summary>Number of workspace dependency edges where this repo is the dependent. Set when dependencies are merged.</summary>
    public int? Dependencies { get; set; }
}
