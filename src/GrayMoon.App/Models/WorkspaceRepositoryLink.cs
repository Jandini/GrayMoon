using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace GrayMoon.App.Models;

[Table("WorkspaceRepositories")]
public class WorkspaceRepositoryLink
{
    /// <summary>Primary key for the workspace-repository link row.</summary>
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

    /// <summary>Tag name HEAD is currently checked out at (detached HEAD on a tag). Mutually exclusive with <see cref="BranchName"/>: when this is non-empty, write operations (push/pull/update/commit-sync/sync-to-default) are blocked because the repo is pinned to a fixed version.</summary>
    [MaxLength(200)]
    public string? CheckedOutTag { get; set; }

    /// <summary>True when the repository is currently on a tag (detached HEAD) rather than on a branch. Used by UI to gate write actions.</summary>
    [NotMapped]
    public bool IsOnTag => !string.IsNullOrWhiteSpace(CheckedOutTag);

    /// <summary>True when the repository is on a tag and at least one newer tag exists (i.e. the checked-out tag is not the most recently created). Null when unknown or not on a tag. Updated when tags are fetched during checkout sync.</summary>
    public bool? HasNewerTag { get; set; }

    /// <summary>Repository's default branch name (e.g. main, master, develop). Set during sync from agent and reused for divergence / PR URLs and sync-to-default logic.</summary>
    [MaxLength(200)]
    public string? DefaultBranchName { get; set; }

    /// <summary>Number of .csproj projects in the repository (excludes .git). Set during sync.</summary>
    public int? Projects { get; set; }

    /// <summary>Outgoing commits (ahead of remote). Set during sync after fetch.</summary>
    public int? OutgoingCommits { get; set; }

    /// <summary>Incoming commits (behind remote). Set during sync after fetch.</summary>
    public int? IncomingCommits { get; set; }

    /// <summary>Commits on default branch not in current branch (vs default, for Divergence column). Set during sync.</summary>
    public int? DefaultBranchBehindCommits { get; set; }

    /// <summary>Commits on current branch not in default branch (vs default, for Divergence column). Set during sync.</summary>
    public int? DefaultBranchAheadCommits { get; set; }

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

    /// <summary>Count of configured version-file lines whose current value does not match the expected repo GitVersion. Set when file version status is checked (same trigger as dep stats). Used to extend the badge X-of-Y count.</summary>
    public int? OutOfDateFileLines { get; set; }

    /// <summary>Count of distinct workspace repositories referenced by out-of-date version-file tokens. Set when file version status is checked.</summary>
    public int? OutOfDateFileRepos { get; set; }

    /// <summary>Total count of configured version-file lines that were matched in files (regardless of whether they are up to date). Set when file version status is checked. Used for badge Y denominator.</summary>
    public int? TotalFileLines { get; set; }

    /// <summary>Dominant project type for this repository (Service &gt; Package &gt; Executable &gt; Library &gt; Test). Null when no projects are known. Set during sync and project refresh.</summary>
    public ProjectType? RepositoryType { get; set; }

    /// <summary>Persisted pull request state for this workspace-repo link. 1:1 optional.</summary>
    public WorkspaceRepositoryPullRequest? PullRequest { get; set; }

    /// <summary>Persisted CI action status for this workspace-repo link. 1:1 optional.</summary>
    public WorkspaceRepositoryAction? Action { get; set; }
}
