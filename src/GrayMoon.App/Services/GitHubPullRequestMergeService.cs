using GrayMoon.App.Models;

namespace GrayMoon.App.Services;

/// <summary>
/// Assembles a fresh, on-demand mergeability snapshot for the merge confirmation dialog and performs the merge itself.
/// Every call in this service hits GitHub live (no ETag/read cache) so the dialog and the merge action always reflect
/// GitHub's current authoritative state - branch protection, required reviews, checks, conflicts, and merge queues are
/// never re-derived locally.
/// </summary>
public sealed class GitHubPullRequestMergeService(
    GitHubService gitHubService,
    ILogger<GitHubPullRequestMergeService> logger)
{
    private static readonly MergeMethod[] DefaultMethodPriority = [MergeMethod.Squash, MergeMethod.Merge, MergeMethod.Rebase];

    /// <summary>Fetches everything the merge dialog needs. Returns null when the repository/connector is not GitHub-backed or the PR cannot be found.</summary>
    public async Task<PullRequestMergeDetails?> GetMergeDetailsAsync(
        Repository repository,
        Connector? connector,
        int prNumber,
        bool hasUncommittedChanges = false,
        int uncommittedChangesCount = 0,
        int unpushedCommitsCount = 0,
        int incomingCommitsCount = 0,
        CancellationToken cancellationToken = default)
    {
        if (repository == null || prNumber <= 0)
            return null;
        if (connector == null || connector.ConnectorType != ConnectorType.GitHub || string.IsNullOrWhiteSpace(connector.UserToken))
            return null;
        if (!RepositoryUrlHelper.TryParseGitHubOwnerRepo(repository.CloneUrl, out var owner, out var repo) || owner == null || repo == null)
            return null;

        var pr = await gitHubService.GetPullRequestByNumberAsync(connector, owner, repo, prNumber, cancellationToken);
        if (pr == null)
            return null;

        var headSha = pr.Head?.Sha;

        var reviewsTask = gitHubService.GetPullRequestReviewsAsync(connector, owner, repo, prNumber, cancellationToken);
        var settingsTask = gitHubService.GetRepositoryMergeSettingsAsync(connector, owner, repo, cancellationToken);
        var checksTask = string.IsNullOrWhiteSpace(headSha)
            ? Task.FromResult<GitHubCheckRunsResponse?>(null)
            : gitHubService.GetCheckRunsForRefAsync(connector, owner, repo, headSha, cancellationToken);

        await Task.WhenAll(reviewsTask, settingsTask, checksTask);

        var reviews = reviewsTask.Result;
        var settings = settingsTask.Result;
        var checkRuns = checksTask.Result;

        var (approvedCount, changesRequestedCount, outstandingReviewers, approvedByUsers) = SummarizeReviews(reviews, pr.RequestedReviewers);
        var checksSummary = SummarizeChecks(checkRuns);
        var allowedMethods = GetAllowedMergeMethods(settings);
        var defaultMethod = DefaultMethodPriority.FirstOrDefault(m => allowedMethods.Contains(m), allowedMethods.FirstOrDefault());

        return new PullRequestMergeDetails
        {
            Number = pr.Number,
            Title = pr.Title ?? string.Empty,
            HeadRef = pr.Head?.Ref ?? string.Empty,
            BaseRef = pr.Base?.Ref ?? string.Empty,
            HeadSha = headSha,
            HtmlUrl = pr.HtmlUrl ?? string.Empty,
            ChangedFiles = pr.ChangedFiles,
            Mergeable = pr.Mergeable,
            MergeableState = pr.MergeableState,
            ApprovedCount = approvedCount,
            ChangesRequestedCount = changesRequestedCount,
            OutstandingReviewers = outstandingReviewers,
            ApprovedByUsers = approvedByUsers,
            Checks = checksSummary,
            AllowedMergeMethods = allowedMethods,
            DefaultMergeMethod = allowedMethods.Count > 0 ? defaultMethod : null,
            HasUncommittedChanges = hasUncommittedChanges,
            UncommittedChangesCount = uncommittedChangesCount,
            UnpushedCommitsCount = unpushedCommitsCount,
            IncomingCommitsCount = incomingCommitsCount
        };
    }

    /// <summary>Merges the pull request via GitHub's merge endpoint. GitHub alone decides success/failure - this never pre-validates mergeability locally.</summary>
    public async Task<MergeResult> MergePullRequestAsync(Repository repository, Connector? connector, int prNumber, MergeMethod method, string? expectedHeadSha, CancellationToken cancellationToken = default)
    {
        if (repository == null || prNumber <= 0)
            return new MergeResult(false, "Invalid repository or pull request.");
        if (connector == null || connector.ConnectorType != ConnectorType.GitHub || string.IsNullOrWhiteSpace(connector.UserToken))
            return new MergeResult(false, "This repository is not connected to GitHub.");
        if (!RepositoryUrlHelper.TryParseGitHubOwnerRepo(repository.CloneUrl, out var owner, out var repo) || owner == null || repo == null)
            return new MergeResult(false, "Could not resolve the GitHub owner/repository from the clone URL.");

        try
        {
            var response = await gitHubService.MergePullRequestAsync(connector, owner, repo, prNumber, method.ToGitHubValue(), expectedHeadSha, cancellationToken);
            return new MergeResult(response.Merged, response.Message);
        }
        catch (HttpRequestException ex)
        {
            var friendly = GitHubApiErrorHelper.FormatFriendlyGitHubHttpError(ex);
            logger.LogWarning(ex, "Merge PR failed for {Owner}/{Repo} PR #{Number}", owner, repo, prNumber);
            return new MergeResult(false, friendly);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Merge PR errored for {Owner}/{Repo} PR #{Number}", owner, repo, prNumber);
            return new MergeResult(false, ex.Message);
        }
    }

    private static (int Approved, int ChangesRequested, IReadOnlyList<string> Outstanding, IReadOnlyList<string> ApprovedBy) SummarizeReviews(
        List<GitHubPullRequestReviewDto> reviews,
        List<GitHubUserDto>? requestedReviewers)
    {
        // GitHub returns one row per review event; only the latest review per user reflects their current stance.
        var latestByUser = reviews
            .Where(r => !string.IsNullOrWhiteSpace(r.User?.Login))
            .GroupBy(r => r.User!.Login!, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderBy(r => r.SubmittedAt ?? DateTimeOffset.MinValue).Last())
            .ToList();

        var approvedByUsers = latestByUser
            .Where(r => string.Equals(r.State, "APPROVED", StringComparison.OrdinalIgnoreCase))
            .Select(r => r.User!.Login!)
            .ToList();
        var changesRequested = latestByUser.Count(r => string.Equals(r.State, "CHANGES_REQUESTED", StringComparison.OrdinalIgnoreCase));

        var approvedLogins = approvedByUsers.ToHashSet(StringComparer.OrdinalIgnoreCase);

        var outstanding = (requestedReviewers ?? [])
            .Where(u => !string.IsNullOrWhiteSpace(u.Login) && !approvedLogins.Contains(u.Login!))
            .Select(u => u.Login!)
            .ToList();

        return (approvedByUsers.Count, changesRequested, outstanding, approvedByUsers);
    }

    private static ChecksSummary SummarizeChecks(GitHubCheckRunsResponse? response)
    {
        var runs = response?.CheckRuns ?? [];
        if (runs.Count == 0)
            return new ChecksSummary { State = ChecksState.None };

        var pending = runs.Count(r => !string.Equals(r.Status, "completed", StringComparison.OrdinalIgnoreCase));
        var failed = runs.Count(r => string.Equals(r.Status, "completed", StringComparison.OrdinalIgnoreCase)
            && r.Conclusion is not null
            && !string.Equals(r.Conclusion, "success", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(r.Conclusion, "neutral", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(r.Conclusion, "skipped", StringComparison.OrdinalIgnoreCase));
        var passed = runs.Count - pending - failed;

        var state = failed > 0 ? ChecksState.Failed : pending > 0 ? ChecksState.Pending : ChecksState.Passed;
        return new ChecksSummary { Passed = passed, Failed = failed, Pending = pending, State = state };
    }

    /// <summary>Missing/null flags are treated as allowed, matching GitHub's own default-visible behavior for API responses that omit these fields (e.g. reduced token scope).</summary>
    private static IReadOnlyList<MergeMethod> GetAllowedMergeMethods(GitHubRepositoryMergeSettingsDto? settings)
    {
        if (settings == null)
            return Array.Empty<MergeMethod>();

        var allowed = new List<MergeMethod>();
        if (settings.AllowMergeCommit != false)
            allowed.Add(MergeMethod.Merge);
        if (settings.AllowSquashMerge != false)
            allowed.Add(MergeMethod.Squash);
        if (settings.AllowRebaseMerge != false)
            allowed.Add(MergeMethod.Rebase);
        return allowed;
    }
}
