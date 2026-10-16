namespace GrayMoon.App.Models;

public class RepoGitVersionInfo
{
    public string Version { get; init; } = "-";
    public string Branch { get; init; } = "-";
    /// <summary>Tag the repository is currently checked out at (detached HEAD). Null when on a branch.</summary>
    public string? Tag { get; init; }

    /// <summary>Full commit hash of HEAD when <see cref="Tag"/> is set.</summary>
    public string? Commit { get; init; }
    /// <summary>Tags discovered during sync (newest first when supported).</summary>
    public IReadOnlyList<string>? Tags { get; init; }
    public int? Projects { get; init; }
    /// <summary>Outgoing commits (ahead of remote).</summary>
    public int? OutgoingCommits { get; init; }
    /// <summary>Incoming commits (behind remote).</summary>
    public int? IncomingCommits { get; init; }
    /// <summary>Commits on default branch not in current branch (vs default, for Divergence column).</summary>
    public int? DefaultBranchBehindCommits { get; init; }
    /// <summary>Commits on current branch not in default branch (vs default, for Divergence column).</summary>
    public int? DefaultBranchAheadCommits { get; init; }
    /// <summary>Full project list from sync (for merge persistence).</summary>
    public IReadOnlyList<SyncProjectInfo>? ProjectsDetail { get; init; }
    /// <summary>Local branches fetched during sync.</summary>
    public IReadOnlyList<string>? LocalBranches { get; init; }
    /// <summary>Remote branches fetched during sync.</summary>
    public IReadOnlyList<string>? RemoteBranches { get; init; }
    /// <summary>Default branch name (e.g. main or master) from sync; used to persist IsDefault.</summary>
    public string? DefaultBranch { get; init; }
    /// <summary>True if current branch has an upstream (remote) branch. Set from RefreshRepositoryVersion / SyncCommand.</summary>
    public bool? HasUpstream { get; init; }
    /// <summary>Error message when sync failed for this repo. Never contains an SSO authorization URL.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>True when this sync attempt failed (clone or fetch). A GitVersion warning leaves this false.</summary>
    public bool FatalSyncFailure { get; init; }

    /// <summary>True when <see cref="FatalSyncFailure"/> left no checkout.</summary>
    public bool NotCloned { get; init; }

    /// <summary>True when git reported GitHub SAML SSO. The row can offer authorization when <see cref="SsoAuthorizationUrl"/> is set.</summary>
    public bool SsoAuthorizationRequired { get; init; }

    /// <summary>Ephemeral validated GitHub SSO URL. Not copied onto <see cref="Snapshot"/>.</summary>
    public string? SsoAuthorizationUrl { get; init; }

    public string? SsoOAuthApplication { get; init; }

    public string? SsoOrganization { get; init; }

    /// <summary>
    /// Live grid status for this attempt. A fatal failure is never in sync just because <see cref="ErrorMessage"/> is set.
    /// A successful attempt with a branch or version is in sync, including a nonfatal version warning.
    /// </summary>
    public RepoSyncStatus ToProgressStatus()
    {
        if (FatalSyncFailure)
            return NotCloned ? RepoSyncStatus.NotCloned : RepoSyncStatus.Error;
        if (!string.IsNullOrWhiteSpace(Branch) && Branch != "-")
            return RepoSyncStatus.InSync;
        if (!string.IsNullOrWhiteSpace(Version) && Version != "-")
            return RepoSyncStatus.InSync;
        return RepoSyncStatus.Error;
    }

    /// <summary>
    /// The same state expressed with per-group probe markers, for persistence through
    /// <see cref="GrayMoon.Abstractions.Notifications.RepositoryStateSnapshot"/>. The flat properties above
    /// stay for the callers that only read values; the snapshot is what decides which columns may be replaced.
    /// Null when the response could not be parsed at all.
    /// </summary>
    public Abstractions.Notifications.RepositoryStateSnapshot? Snapshot { get; init; }
}
