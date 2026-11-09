using GrayMoon.App.Models;
using GrayMoon.App.Services.GitHub;

namespace GrayMoon.App.Services.GitProviders;

/// <summary>GitHub implementation of <see cref="IGitProvider"/>, delegating to the existing GitHub pull request service.</summary>
public sealed class GitHubGitProvider(GitHubPullRequestService pullRequests) : IGitProvider
{
    public ConnectorType ConnectorType => ConnectorType.GitHub;

    public Task<PullRequestInfo?> FindPullRequestForBranchAsync(
        Connector connector, string targetCloneUrl, string headCloneUrl, string branch, CancellationToken cancellationToken = default) =>
        pullRequests.GetPullRequestForBranchAsync(connector, targetCloneUrl, headCloneUrl, branch, cancellationToken);

    public Task<PullRequestInfo?> GetPullRequestByNumberAsync(
        Connector connector, string targetCloneUrl, int number, string? headBranch, CancellationToken cancellationToken = default) =>
        pullRequests.GetPullRequestByNumberAsync(connector, targetCloneUrl, number, headBranch, cancellationToken);
}

/// <summary>Provider for connector types that have no pull requests (for example NuGet): every lookup finds nothing.</summary>
public sealed class NoGitProvider : IGitProvider
{
    public static NoGitProvider Instance { get; } = new();

    private NoGitProvider() { }

    public ConnectorType ConnectorType => 0;

    public Task<PullRequestInfo?> FindPullRequestForBranchAsync(
        Connector connector, string targetCloneUrl, string headCloneUrl, string branch, CancellationToken cancellationToken = default) =>
        Task.FromResult<PullRequestInfo?>(null);

    public Task<PullRequestInfo?> GetPullRequestByNumberAsync(
        Connector connector, string targetCloneUrl, int number, string? headBranch, CancellationToken cancellationToken = default) =>
        Task.FromResult<PullRequestInfo?>(null);
}

/// <remarks>An unknown value (a newer database read by an older build) selects the provider that finds nothing.</remarks>
public sealed class GitProviderResolver(GitHubGitProvider gitHubProvider) : IGitProviderResolver
{
    public IGitProvider Get(ConnectorType connectorType) => connectorType switch
    {
        ConnectorType.GitHub => gitHubProvider,
        _ => NoGitProvider.Instance,
    };
}
