namespace GrayMoon.App.Models;

/// <summary>Overall check-run conclusion state for the merge dialog's compact checks summary.</summary>
public enum ChecksState
{
    /// <summary>No checks reported for this commit.</summary>
    None,
    Passed,
    Failed,
    Pending
}

/// <summary>Aggregate check-run counts, never the individual check names/logs - the merge dialog only shows a compact summary.</summary>
public sealed class ChecksSummary
{
    public int Passed { get; init; }
    public int Failed { get; init; }
    public int Pending { get; init; }
    public ChecksState State { get; init; } = ChecksState.None;
}

/// <summary>
/// Fresh, on-demand snapshot of a pull request's mergeability assembled from live GitHub data (PR, reviews, check-runs,
/// repository merge settings) when the merge dialog opens. Never derived from the polled/cached <see cref="PullRequestInfo"/>
/// used for the grid badge - GitHub is the sole source of truth for whether/how a merge can proceed.
/// </summary>
public sealed class PullRequestMergeDetails
{
    public int Number { get; init; }
    public string Title { get; init; } = string.Empty;
    public string HeadRef { get; init; } = string.Empty;
    public string BaseRef { get; init; } = string.Empty;
    public string? HeadSha { get; init; }
    public string HtmlUrl { get; init; } = string.Empty;
    public int? ChangedFiles { get; init; }

    /// <summary>True = mergeable, false = conflict, null = unknown (GitHub is still computing mergeability).</summary>
    public bool? Mergeable { get; init; }
    /// <summary>e.g. unknown, clean, dirty, unstable, blocked, draft, behind.</summary>
    public string? MergeableState { get; init; }

    public int ApprovedCount { get; init; }
    public int ChangesRequestedCount { get; init; }
    public IReadOnlyList<string> OutstandingReviewers { get; init; } = Array.Empty<string>();
    /// <summary>Logins of reviewers whose latest review is an approval, for the dialog's "Approved by ..." subtitle.</summary>
    public IReadOnlyList<string> ApprovedByUsers { get; init; } = Array.Empty<string>();

    public ChecksSummary Checks { get; init; } = new();

    /// <summary>
    /// GrayMoon-local check (not from GitHub): true when the workspace's local clone has uncommitted changes
    /// and/or commits that have not been pushed to the remote yet. Purely informational - unlike the GitHub
    /// checks above, this never blocks the merge button, since merging on GitHub does not require the local
    /// clone to be clean or in sync.
    /// </summary>
    public bool HasUncommittedChanges { get; init; }
    public int UncommittedChangesCount { get; init; }
    /// <summary>Commits ahead of the remote (not yet pushed).</summary>
    public int UnpushedCommitsCount { get; init; }
    /// <summary>Commits behind the remote (not yet pulled locally).</summary>
    public int IncomingCommitsCount { get; init; }
    /// <summary>True when any local signal above is set - drives the warning icon on the local-state row.</summary>
    public bool HasLocalWarning => HasUncommittedChanges || UnpushedCommitsCount > 0 || IncomingCommitsCount > 0;

    /// <summary>True when the merge button itself should render as an orange "proceed with caution" warning: either the local clone has unsynced work, or checks are still running (mergeable_state can flip to "unstable"/blocked once they finish).</summary>
    public bool HasMergeWarning => HasLocalWarning || Checks.State == ChecksState.Pending;

    /// <summary>Merge methods GitHub currently permits for this repository. Only these may ever be offered to the user.</summary>
    public IReadOnlyList<MergeMethod> AllowedMergeMethods { get; init; } = Array.Empty<MergeMethod>();
    /// <summary>Best-effort preselected method (GitHub's REST API exposes no explicit "preferred method" flag) - Squash, then Merge, then Rebase, filtered to <see cref="AllowedMergeMethods"/>.</summary>
    public MergeMethod? DefaultMergeMethod { get; init; }

    /// <summary>Human-readable reasons the merge is currently blocked, derived only from GitHub's own reported state. Empty when mergeable.</summary>
    public IReadOnlyList<string> BlockingReasons { get; init; } = Array.Empty<string>();

    /// <summary>True when GitHub reports the PR as currently mergeable via any allowed method.</summary>
    public bool CanMergeNow => Mergeable == true && AllowedMergeMethods.Count > 0;
}

/// <summary>Result of a merge attempt. <see cref="Message"/> is always GitHub's own error text on failure - GrayMoon never invents a reason.</summary>
public sealed record MergeResult(bool Success, string? Message);
