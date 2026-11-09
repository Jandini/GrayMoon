namespace GrayMoon.App.Models;

/// <summary>Display model for a repository's pull request state (from the Git provider API).</summary>
public sealed class PullRequestInfo
{
    public int Number { get; set; }
    public string State { get; set; } = string.Empty; // "open", "closed"
    public bool IsMerged => MergedAt.HasValue;
    public bool IsClosed => string.Equals(State, "closed", StringComparison.OrdinalIgnoreCase);
    public DateTimeOffset? MergedAt { get; set; }
    /// <summary>Commit the pull request merged (<c>head.sha</c>). For a squash or rebase this is the feature tip, not the commit added to the base branch.</summary>
    public string? HeadSha { get; set; }
    public string HtmlUrl { get; set; } = string.Empty;
    public int? ChangedFiles { get; set; }
    /// <summary>True = mergeable, false = conflict, null = unknown.</summary>
    public bool? Mergeable { get; set; }
    /// <summary>e.g. unknown, clean, dirty, unstable, blocked.</summary>
    public string? MergeableState { get; set; }

    /// <summary>
    /// True when this pull request lives in the repository the cloned repository was forked from (upstream) rather than in the
    /// cloned repository itself. Such a pull request is link-only: GrayMoon never merges, closes or edits it, because those
    /// calls target the cloned repository and normally need write access the user does not have upstream.
    /// </summary>
    public bool IsInParentRepository { get; set; }

    /// <summary>
    /// For a fork whose own repository and upstream repository both have a pull request for the branch: the upstream one
    /// (this instance is then the pull request in the fork itself). Always null on an upstream pull request.
    /// </summary>
    public PullRequestInfo? ParentPullRequest { get; set; }

    /// <summary>
    /// Combines the pull request found in the cloned repository (<paramref name="own"/>) with the one found upstream
    /// (<paramref name="parent"/>): both gives <paramref name="own"/> carrying <see cref="ParentPullRequest"/>; only one gives
    /// that pull request (flagged <see cref="IsInParentRepository"/> when it is the upstream one); none gives null.
    /// The inputs are not modified.
    /// </summary>
    public static PullRequestInfo? Compose(PullRequestInfo? own, PullRequestInfo? parent)
    {
        if (own != null && parent != null)
            return own.CloneWith(isInParent: false, parentPullRequest: parent.CloneWith(isInParent: true, parentPullRequest: null));
        if (own != null)
            return own.CloneWith(isInParent: false, parentPullRequest: null);
        if (parent != null)
            return parent.CloneWith(isInParent: true, parentPullRequest: null);
        return null;
    }

    private PullRequestInfo CloneWith(bool isInParent, PullRequestInfo? parentPullRequest) => new()
    {
        Number = Number,
        State = State,
        MergedAt = MergedAt,
        HeadSha = HeadSha,
        HtmlUrl = HtmlUrl,
        ChangedFiles = ChangedFiles,
        Mergeable = Mergeable,
        MergeableState = MergeableState,
        IsInParentRepository = isInParent,
        ParentPullRequest = parentPullRequest,
    };
}

/// <summary>The pull request for one branch in the cloned repository and, for a fork, in its upstream repository.</summary>
public sealed record BranchPullRequests(PullRequestInfo? Own, PullRequestInfo? Parent)
{
    public static BranchPullRequests None { get; } = new(null, null);

    /// <summary>The single value the grid shows; see <see cref="PullRequestInfo.Compose"/>.</summary>
    public PullRequestInfo? Compose() => PullRequestInfo.Compose(Own, Parent);
}
