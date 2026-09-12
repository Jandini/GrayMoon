using System.Net;
using System.Text.Json;
using GrayMoon.Abstractions.Models;
using GrayMoon.App.Models;

namespace GrayMoon.App.Services.GitHub;

/// <summary>Pull requests, reviews, merge, teams/collaborators, and check-run status.</summary>
public sealed partial class GitHubService
{
    /// <summary>Gets the pull request for the given branch in the repo, if any. Fetches up to 5 and returns the first one opened by a human (user.type != "Bot"), falling back to the first match when all are bots. Returns null when no PR or API error.</summary>
    public async Task<GitHubPullRequestDto?> GetPullRequestForBranchAsync(Connector connector, string owner, string repo, string branch, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(owner))
            throw new ArgumentException("Owner is required.", nameof(owner));
        if (string.IsNullOrWhiteSpace(repo))
            throw new ArgumentException("Repository is required.", nameof(repo));
        if (string.IsNullOrWhiteSpace(branch))
            return null;

        EnsureConnectorConfigured(connector);

        var head = $"{owner}:{Uri.EscapeDataString(branch)}";
        var requestUri = $"repos/{owner}/{repo}/pulls?state=all&head={head}&per_page=5";

        try
        {
            var list = await GetETaggedAsync<List<GitHubPullRequestDto>>(connector, requestUri, cancellationToken);
            if (list == null || list.Count == 0)
                return null;

            var matching = list.Where(pr => pr.Head == null || string.Equals(pr.Head.Ref, branch, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matching.Count == 0)
                return null;

            // Prefer a PR opened by a human; fall back to first match if all are bots
            return matching.FirstOrDefault(pr => !string.Equals(pr.User?.Type, "Bot", StringComparison.OrdinalIgnoreCase))
                ?? matching[0];
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "GitHub API get PR for branch failed. Owner={Owner}, Repo={Repo}, Branch={Branch}", owner, repo, branch);
            return null;
        }
    }

    /// <summary>Gets a single pull request by number. Uses GET /repos/{owner}/{repo}/pulls/{pull_number}. Returns null on API error.</summary>
    public async Task<GitHubPullRequestDto?> GetPullRequestByNumberAsync(Connector connector, string owner, string repo, int pullNumber, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(owner))
            throw new ArgumentException("Owner is required.", nameof(owner));
        if (string.IsNullOrWhiteSpace(repo))
            throw new ArgumentException("Repository is required.", nameof(repo));
        if (pullNumber <= 0)
            return null;

        EnsureConnectorConfigured(connector);

        var requestUri = $"repos/{owner}/{repo}/pulls/{pullNumber}";

        try
        {
            return await GetETaggedAsync<GitHubPullRequestDto>(connector, requestUri, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "GitHub API get PR by number failed. Owner={Owner}, Repo={Repo}, PullNumber={PullNumber}", owner, repo, pullNumber);
            return null;
        }
    }

    /// <summary>Closes an open pull request via PATCH /repos/{owner}/{repo}/pulls/{pull_number}. Throws on non-success.</summary>
    public async Task ClosePullRequestAsync(
        Connector connector,
        string owner,
        string repo,
        int pullNumber,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(owner))
            throw new ArgumentException("Owner is required.", nameof(owner));
        if (string.IsNullOrWhiteSpace(repo))
            throw new ArgumentException("Repository is required.", nameof(repo));
        if (pullNumber <= 0)
            throw new ArgumentOutOfRangeException(nameof(pullNumber));

        EnsureConnectorConfigured(connector);

        var requestUri = $"repos/{owner}/{repo}/pulls/{pullNumber}";
        var payload = "{\"state\":\"closed\"}";
        await PatchAsync(connector, requestUri, payload, cancellationToken);
    }

    /// <summary>Updates only the pull request title via PATCH /repos/{owner}/{repo}/pulls/{pull_number}. Throws on non-success.</summary>
    public async Task UpdatePullRequestTitleAsync(
        Connector connector,
        string owner,
        string repo,
        int pullNumber,
        string title,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(owner))
            throw new ArgumentException("Owner is required.", nameof(owner));
        if (string.IsNullOrWhiteSpace(repo))
            throw new ArgumentException("Repository is required.", nameof(repo));
        if (pullNumber <= 0)
            throw new ArgumentOutOfRangeException(nameof(pullNumber));
        if (string.IsNullOrWhiteSpace(title))
            throw new ArgumentException("Title is required.", nameof(title));

        EnsureConnectorConfigured(connector);

        var body = new GitHubUpdatePullRequestTitleRequestDto { Title = title.Trim() };
        var requestUri = $"repos/{owner}/{repo}/pulls/{pullNumber}";
        var payload = JsonSerializer.Serialize(body);
        await PatchAsync(connector, requestUri, payload, cancellationToken);
    }

    /// <summary>Creates a pull request via POST /repos/{owner}/{repo}/pulls. Throws on non-success.</summary>
    public async Task<GitHubPullRequestDto> CreatePullRequestAsync(
        Connector connector,
        string owner,
        string repo,
        GitHubCreatePullRequestRequestDto body,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(owner))
            throw new ArgumentException("Owner is required.", nameof(owner));
        if (string.IsNullOrWhiteSpace(repo))
            throw new ArgumentException("Repository is required.", nameof(repo));
        ArgumentNullException.ThrowIfNull(body);

        EnsureConnectorConfigured(connector);

        var requestUri = $"repos/{owner}/{repo}/pulls";
        var payload = JsonSerializer.Serialize(body);

        var dto = await PostAsync<GitHubPullRequestDto>(connector, requestUri, payload, cancellationToken);
        if (dto == null)
            throw new InvalidOperationException("GitHub returned an empty response when creating a pull request.");
        return dto;
    }

    /// <summary>Requests reviewers via POST /repos/{owner}/{repo}/pulls/{pull_number}/requested_reviewers. Throws on non-success.</summary>
    public async Task RequestReviewersAsync(
        Connector connector,
        string owner,
        string repo,
        int pullNumber,
        IReadOnlyList<string> reviewers,
        IReadOnlyList<string> teamReviewers,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(owner))
            throw new ArgumentException("Owner is required.", nameof(owner));
        if (string.IsNullOrWhiteSpace(repo))
            throw new ArgumentException("Repository is required.", nameof(repo));
        if (pullNumber <= 0)
            throw new ArgumentOutOfRangeException(nameof(pullNumber));

        EnsureConnectorConfigured(connector);

        var body = new GitHubRequestReviewersRequestDto
        {
            Reviewers = reviewers?.Where(static r => !string.IsNullOrWhiteSpace(r)).ToList() ?? new List<string>(),
            TeamReviewers = teamReviewers?.Where(static r => !string.IsNullOrWhiteSpace(r)).ToList() ?? new List<string>()
        };
        if (body.Reviewers.Count == 0 && body.TeamReviewers.Count == 0)
            return;

        var requestUri = $"repos/{owner}/{repo}/pulls/{pullNumber}/requested_reviewers";
        var payload = JsonSerializer.Serialize(body);
        await PostAsync(connector, requestUri, payload, cancellationToken);
    }

    /// <summary>Lists teams via GET /repos/{owner}/{repo}/teams. Returns empty list on 403/404.</summary>
    public async Task<List<GitHubTeamDto>> GetTeamsAsync(
        Connector connector,
        string owner,
        string repo,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(owner))
            throw new ArgumentException("Owner is required.", nameof(owner));
        if (string.IsNullOrWhiteSpace(repo))
            throw new ArgumentException("Repository is required.", nameof(repo));

        EnsureConnectorConfigured(connector);

        var requestUri = $"repos/{owner}/{repo}/teams?per_page=100";

        try
        {
            return await GetAsync<List<GitHubTeamDto>>(connector, requestUri, cancellationToken)
                ?? new List<GitHubTeamDto>();
        }
        catch (GitHubHttpRequestException ex) when (ex.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
        {
            _logger.LogDebug(ex, "GitHub API list teams failed (treated as empty). Owner={Owner}, Repo={Repo}", owner, repo);
            return new List<GitHubTeamDto>();
        }
    }

    /// <summary>Lists collaborators via GET /repos/{owner}/{repo}/collaborators. Returns empty list on 403/404.</summary>
    public async Task<List<GitHubCollaboratorDto>> GetCollaboratorsAsync(
        Connector connector,
        string owner,
        string repo,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(owner))
            throw new ArgumentException("Owner is required.", nameof(owner));
        if (string.IsNullOrWhiteSpace(repo))
            throw new ArgumentException("Repository is required.", nameof(repo));

        EnsureConnectorConfigured(connector);

        var requestUri = $"repos/{owner}/{repo}/collaborators?affiliation=all&per_page=100";

        try
        {
            return await GetAsync<List<GitHubCollaboratorDto>>(connector, requestUri, cancellationToken)
                ?? new List<GitHubCollaboratorDto>();
        }
        catch (GitHubHttpRequestException ex) when (ex.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
        {
            _logger.LogDebug(ex, "GitHub API list collaborators failed (treated as empty). Owner={Owner}, Repo={Repo}", owner, repo);
            return new List<GitHubCollaboratorDto>();
        }
    }

    /// <summary>Gets the repository's permitted merge methods via GET /repos/{owner}/{repo}, ETag-conditional so repeat calls (dialog re-open, in-dialog poll refresh) come back as a cheap 304 when settings haven't changed. Returns null on API error - callers must not assume any method is allowed when this fails.</summary>
    public async Task<GitHubRepositoryMergeSettingsDto?> GetRepositoryMergeSettingsAsync(
        Connector connector,
        string owner,
        string repo,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(owner))
            throw new ArgumentException("Owner is required.", nameof(owner));
        if (string.IsNullOrWhiteSpace(repo))
            throw new ArgumentException("Repository is required.", nameof(repo));

        EnsureConnectorConfigured(connector);

        var requestUri = $"repos/{owner}/{repo}";

        try
        {
            return await GetETaggedAsync<GitHubRepositoryMergeSettingsDto>(connector, requestUri, cancellationToken, skipRateLimitRetry: true);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "GitHub API get repository merge settings failed. Owner={Owner}, Repo={Repo}", owner, repo);
            return null;
        }
    }

    /// <summary>Lists reviews via GET /repos/{owner}/{repo}/pulls/{pull_number}/reviews, ETag-conditional so repeat calls (dialog re-open, in-dialog poll refresh) come back as a cheap 304 when reviews haven't changed. Returns empty list on API error.</summary>
    public async Task<List<GitHubPullRequestReviewDto>> GetPullRequestReviewsAsync(
        Connector connector,
        string owner,
        string repo,
        int pullNumber,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(owner))
            throw new ArgumentException("Owner is required.", nameof(owner));
        if (string.IsNullOrWhiteSpace(repo))
            throw new ArgumentException("Repository is required.", nameof(repo));
        if (pullNumber <= 0)
            return new List<GitHubPullRequestReviewDto>();

        EnsureConnectorConfigured(connector);

        var requestUri = $"repos/{owner}/{repo}/pulls/{pullNumber}/reviews?per_page=100";

        try
        {
            return await GetETaggedAsync<List<GitHubPullRequestReviewDto>>(connector, requestUri, cancellationToken, skipRateLimitRetry: true)
                ?? new List<GitHubPullRequestReviewDto>();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "GitHub API list PR reviews failed. Owner={Owner}, Repo={Repo}, PullNumber={PullNumber}", owner, repo, pullNumber);
            return new List<GitHubPullRequestReviewDto>();
        }
    }

    /// <summary>Gets check-run summary via GET /repos/{owner}/{repo}/commits/{ref}/check-runs, ETag-conditional so repeat calls (dialog re-open, in-dialog poll refresh) come back as a cheap 304 when checks haven't changed. Returns null on API error - callers must treat checks as unknown, not as passing.</summary>
    public async Task<GitHubCheckRunsResponse?> GetCheckRunsForRefAsync(
        Connector connector,
        string owner,
        string repo,
        string sha,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(owner))
            throw new ArgumentException("Owner is required.", nameof(owner));
        if (string.IsNullOrWhiteSpace(repo))
            throw new ArgumentException("Repository is required.", nameof(repo));
        if (string.IsNullOrWhiteSpace(sha))
            return null;

        EnsureConnectorConfigured(connector);

        var requestUri = $"repos/{owner}/{repo}/commits/{sha}/check-runs?per_page=100";

        try
        {
            return await GetETaggedAsync<GitHubCheckRunsResponse>(connector, requestUri, cancellationToken, skipRateLimitRetry: true);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "GitHub API get check-runs failed. Owner={Owner}, Repo={Repo}, Sha={Sha}", owner, repo, sha);
            return null;
        }
    }

    /// <summary>
    /// Merges a pull request via PUT /repos/{owner}/{repo}/pulls/{pull_number}/merge. GitHub is the sole authority on whether
    /// the merge succeeds - this call performs no local mergeability check and passes the expected head <paramref name="expectedHeadSha"/>
    /// (when known) so GitHub rejects (409) if the branch changed since it was last read. Throws <see cref="GitHubHttpRequestException"/>
    /// with GitHub's own message on failure (e.g. 405 not mergeable, 409 sha mismatch/conflict, 404 unknown PR).
    /// </summary>
    public async Task<GitHubMergePullRequestResponseDto> MergePullRequestAsync(
        Connector connector,
        string owner,
        string repo,
        int pullNumber,
        string mergeMethod,
        string? expectedHeadSha,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(owner))
            throw new ArgumentException("Owner is required.", nameof(owner));
        if (string.IsNullOrWhiteSpace(repo))
            throw new ArgumentException("Repository is required.", nameof(repo));
        if (pullNumber <= 0)
            throw new ArgumentOutOfRangeException(nameof(pullNumber));
        if (string.IsNullOrWhiteSpace(mergeMethod))
            throw new ArgumentException("Merge method is required.", nameof(mergeMethod));

        EnsureConnectorConfigured(connector);

        var body = new GitHubMergePullRequestRequestDto
        {
            MergeMethod = mergeMethod,
            Sha = string.IsNullOrWhiteSpace(expectedHeadSha) ? null : expectedHeadSha
        };
        var requestUri = $"repos/{owner}/{repo}/pulls/{pullNumber}/merge";
        var payload = JsonSerializer.Serialize(body);

        var dto = await PutAsync<GitHubMergePullRequestResponseDto>(connector, requestUri, payload, cancellationToken);
        if (dto == null)
            throw new InvalidOperationException("GitHub returned an empty response when merging the pull request.");
        return dto;
    }
}
