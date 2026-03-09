using GrayMoon.App.Models;
using GrayMoon.App.Repositories;
using Microsoft.Extensions.Options;

namespace GrayMoon.App.Services;

/// <summary>Resolves pull request for a workspace repo's current branch via GitHub API.</summary>
public sealed class GitHubPullRequestService(
    GitHubService gitHubService,
    IOptions<WorkspaceOptions> workspaceOptions,
    ILogger<GitHubPullRequestService> logger)
{
    private int MaxConcurrency => Math.Max(1, workspaceOptions.Value.MaxParallelOperations);

    /// <summary>Fetches PR info for the given repo and branch using the repo's connector. Returns null when not GitHub, connector invalid, or no PR.</summary>
    public async Task<PullRequestInfo?> GetPullRequestForBranchAsync(Repository repository, Connector? connector, string? branchName, CancellationToken cancellationToken = default)
    {
        if (repository == null || string.IsNullOrWhiteSpace(branchName))
            return null;
        if (connector == null || connector.ConnectorType != ConnectorType.GitHub || string.IsNullOrWhiteSpace(connector.UserToken))
            return null;
        if (!RepositoryUrlHelper.TryParseGitHubOwnerRepo(repository.CloneUrl, out var owner, out var repo) || owner == null || repo == null)
            return null;

        try
        {
            var dto = await gitHubService.GetPullRequestForBranchAsync(connector, owner, repo, branchName, cancellationToken);
            if (dto == null)
                return null;

            var mergeable = dto.Mergeable;
            var mergeableState = dto.MergeableState;

            if (string.Equals(dto.State, "open", StringComparison.OrdinalIgnoreCase) &&
                (mergeable == null || string.Equals(mergeableState, "unknown", StringComparison.OrdinalIgnoreCase)))
            {
                var fullPr = await gitHubService.GetPullRequestByNumberAsync(connector, owner, repo, dto.Number, cancellationToken);
                if (fullPr != null)
                {
                    mergeable = fullPr.Mergeable;
                    mergeableState = fullPr.MergeableState;
                }
            }

            return new PullRequestInfo
            {
                Number = dto.Number,
                State = dto.State ?? string.Empty,
                MergedAt = dto.MergedAt,
                HtmlUrl = dto.HtmlUrl ?? string.Empty,
                Mergeable = mergeable,
                MergeableState = mergeableState
            };
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "GetPullRequestForBranch failed. Repo={Repo}, Branch={Branch}", repository.RepositoryName, branchName);
            return null;
        }
    }

    /// <summary>Fetches PR info for all workspace repo links in parallel (bounded concurrency). Returns a dictionary keyed by RepositoryId.</summary>
    public async Task<IReadOnlyDictionary<int, PullRequestInfo?>> GetPullRequestsForWorkspaceAsync(
        IEnumerable<WorkspaceRepositoryLink> links,
        CancellationToken cancellationToken = default)
    {
        var linkList = links.Where(wr => wr.Repository != null && !string.IsNullOrWhiteSpace(wr.BranchName)).ToList();
        if (linkList.Count == 0)
            return new Dictionary<int, PullRequestInfo?>();

        var result = new Dictionary<int, PullRequestInfo?>();
        using var semaphore = new SemaphoreSlim(MaxConcurrency, MaxConcurrency);
        var tasks = linkList.Select(async wr =>
        {
            await semaphore.WaitAsync(cancellationToken);
            try
            {
                var repo = wr.Repository!;
                var connector = repo.Connector;
                var pr = await GetPullRequestForBranchAsync(repo, connector, wr.BranchName, cancellationToken);
                lock (result)
                    result[wr.RepositoryId] = pr;
            }
            finally
            {
                semaphore.Release();
            }
        });
        await Task.WhenAll(tasks);
        return result;
    }
}
