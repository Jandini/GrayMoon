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

    /// <summary>True if the current branch has an upstream (remote branch). False when branch is new and not pushed. Null when unknown (e.g. not yet synced with branch list).</summary>
    public bool? BranchHasUpstream { get; set; }

    /// <summary>Persisted sync status. New links default to <see cref="RepoSyncStatus.NeedsSync"/>.</summary>
    public RepoSyncStatus SyncStatus { get; set; } = RepoSyncStatus.NeedsSync;

    /// <summary>Dependency level (same value = same level / can be built in parallel). Set when dependencies are merged.</summary>
    public int? DependencyLevel { get; set; }

    /// <summary>Number of workspace dependency edges where this repo is the dependent. Set when dependencies are merged.</summary>
    public int? Dependencies { get; set; }

    /// <summary>Number of those dependencies whose version does not match the referenced repo's GitVersion. Set when dependencies are merged. Used for badge (same logic as build).</summary>
    public int? UnmatchedDeps { get; set; }
}
