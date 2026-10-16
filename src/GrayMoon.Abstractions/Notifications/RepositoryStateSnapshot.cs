namespace GrayMoon.Abstractions.Notifications;

/// <summary>
/// One authoritative description of a repository's git state at a point in time, used by every
/// code path that writes the denormalized badge columns on a workspace-repository link.
/// </summary>
/// <remarks>
/// <para>
/// Each group of fields carries its own <c>*Probed</c> marker. A group is written with replace
/// semantics - null really means null - only when its marker is <c>true</c>. When the marker is
/// <c>false</c> the corresponding columns are left exactly as they are.
/// </para>
/// <para>
/// Every marker is a non-nullable <see cref="bool"/> defaulting to <c>false</c>, so a payload from
/// a worker that predates this type deserialises to "nothing was probed" and therefore cannot
/// erase state. Commands that genuinely do not inspect a group (for example a branch-list refresh
/// that never counts commits) leave its marker false for the same reason.
/// </para>
/// </remarks>
public sealed class RepositoryStateSnapshot
{
    /// <summary>Branch currently checked out, or null when HEAD is detached on a tag. Applied when <see cref="IdentityProbed"/>.</summary>
    public string? BranchName { get; init; }

    /// <summary>Tag HEAD is checked out at, or null when on a branch. Applied when <see cref="IdentityProbed"/>.</summary>
    public string? CheckedOutTag { get; init; }

    /// <summary>Full SHA of HEAD. Applied when <see cref="IdentityProbed"/>.</summary>
    public string? HeadCommit { get; init; }

    /// <summary>GitVersion informational version. Applied when <see cref="GitVersionProbed"/>.</summary>
    public string? GitVersion { get; init; }

    /// <summary>Repository default branch without the "origin/" prefix. Written whenever non-empty, independently of any marker.</summary>
    public string? DefaultBranchName { get; init; }

    public int? OutgoingCommits { get; init; }
    public int? IncomingCommits { get; init; }
    public int? DefaultBranchBehind { get; init; }
    public int? DefaultBranchAhead { get; init; }

    /// <summary>Whether the checked-out branch has a configured upstream, from git config rather than a remote-name match. Applied when <see cref="UpstreamProbed"/>.</summary>
    public bool? HasUpstream { get; init; }

    public List<string>? LocalBranches { get; init; }
    public List<string>? RemoteBranches { get; init; }
    public List<string>? Tags { get; init; }

    /// <summary>Projects discovered in the working tree. An empty list with <see cref="ProjectsProbed"/> set means "this branch genuinely has no projects" and prunes.</summary>
    public List<RepositorySyncProjectNotification>? Projects { get; init; }

    /// <summary>Safe error text for this attempt. Never an authorization URL.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// True when this sync attempt failed fatally (clone or fetch). Older payloads omit it, so it defaults to false
    /// and cannot turn a version warning or a hook update into a failure. Probe markers stay false, so prior
    /// branch and version columns are left in place.
    /// </summary>
    public bool FatalSyncFailure { get; init; }

    /// <summary>With <see cref="FatalSyncFailure"/>, the repository has no checkout. Status becomes not cloned.</summary>
    public bool NotCloned { get; init; }

    /// <summary>Branch name and tag state were determined.</summary>
    public bool IdentityProbed { get; init; }

    /// <summary>GitVersion ran and <see cref="GitVersion"/> is authoritative.</summary>
    public bool GitVersionProbed { get; init; }

    /// <summary>
    /// Outgoing and incoming were computed. False when the underlying git command failed, so callers never
    /// infer meaning from a null count. Ahead/behind vs default is only replaced when at least one of those
    /// two values is present; a pair of nulls is treated as "not ready yet" so the previous numbers stay.
    /// </summary>
    public bool CommitCountsProbed { get; init; }

    /// <summary><see cref="HasUpstream"/> was resolved from git config.</summary>
    public bool UpstreamProbed { get; init; }

    /// <summary>The branch, remote-branch and tag lists are complete and may replace what is persisted.</summary>
    public bool BranchesProbed { get; init; }

    /// <summary>The working tree was scanned for projects; <see cref="Projects"/> is complete for this repository.</summary>
    public bool ProjectsProbed { get; init; }

    /// <summary>
    /// True when the only thing probed is the local and remote branch lists, as after a branch was created or deleted
    /// outside GrayMoon. Such a snapshot says nothing about what is checked out or how it compares to its upstream, so
    /// it must not derive sync status, reconcile pull requests or count as a completed sync.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsBranchRefsOnly =>
        BranchesProbed && !IdentityProbed && !GitVersionProbed && !CommitCountsProbed && !UpstreamProbed && !ProjectsProbed;
}
