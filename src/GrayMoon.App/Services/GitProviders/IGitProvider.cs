using GrayMoon.App.Models;

namespace GrayMoon.App.Services.GitProviders;

/// <summary>
/// Source-control provider boundary for pull request lookups across a fork and its upstream repository.
/// One implementation per <see cref="ConnectorType"/> (GitHub today; GitLab or Azure DevOps later). Repositories are
/// addressed by clone URL so the caller never needs provider-specific owner/project parsing, and "pull request" is the
/// neutral term for what a provider may call a merge request. Implementations never throw for "nothing found".
/// </summary>
public interface IGitProvider
{
    /// <summary>The connector type this provider serves.</summary>
    ConnectorType ConnectorType { get; }

    /// <summary>
    /// The pull request for <paramref name="branch"/> that lives in the repository at <paramref name="targetCloneUrl"/>, where the
    /// branch itself lives in the repository at <paramref name="headCloneUrl"/>. The two URLs are equal for the cloned repository
    /// and differ for a fork's upstream (target = parent, head = fork). Returns null when none exists or the lookup failed.
    /// </summary>
    Task<PullRequestInfo?> FindPullRequestForBranchAsync(
        Connector connector, string targetCloneUrl, string headCloneUrl, string branch, CancellationToken cancellationToken = default);

    /// <summary>
    /// A pull request by number in the repository at <paramref name="targetCloneUrl"/>. When <paramref name="headBranch"/> is set,
    /// returns null unless the pull request's head is that branch. Used after a merge deleted the head branch.
    /// </summary>
    Task<PullRequestInfo?> GetPullRequestByNumberAsync(
        Connector connector, string targetCloneUrl, int number, string? headBranch, CancellationToken cancellationToken = default);
}

/// <summary>Maps a connector to the <see cref="IGitProvider"/> that serves it. The only place that maps <see cref="ConnectorType"/> to provider behaviour.</summary>
public interface IGitProviderResolver
{
    /// <summary>Never null: an unsupported connector type resolves to a provider that finds nothing.</summary>
    IGitProvider Get(ConnectorType connectorType);
}
