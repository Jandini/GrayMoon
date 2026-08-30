using GrayMoon.App.Models;

namespace GrayMoon.App.Services.GitHub;

/// <summary>Title/branches/conflicts fields resolved by the fast snapshot phase - just one (now ETag-conditional) GitHub call, everything the dialog's header and Conflicts row need.</summary>
public sealed record PullRequestMergeSnapshot(
    int Number,
    string Title,
    string HeadRef,
    string BaseRef,
    string? HeadSha,
    string HtmlUrl,
    int? ChangedFiles,
    bool? Mergeable,
    string? MergeableState);

/// <summary>Reviews/checks/allowed-methods fields resolved by the background review-details phase (the 3-call `Task.WhenAll` fan-out).</summary>
public sealed record PullRequestMergeReviewData(
    int ApprovedCount,
    int ChangesRequestedCount,
    IReadOnlyList<string> OutstandingReviewers,
    IReadOnlyList<string> ApprovedByUsers,
    ChecksSummary Checks,
    IReadOnlyList<MergeMethod> AllowedMergeMethods,
    MergeMethod? DefaultMergeMethod);

/// <summary>
/// Assembles a fresh, on-demand mergeability snapshot for the merge confirmation dialog and performs the merge itself.
/// Every call in this service hits GitHub live (ETag-conditional, never served from an app-level read cache) so the
/// dialog and the merge action always reflect GitHub's current authoritative state - branch protection, required
/// reviews, checks, conflicts, and merge queues are never re-derived locally; a cheap 304 is still a live answer,
/// just one that costs almost nothing when nothing changed.
/// </summary>
public sealed class GitHubPullRequestMergeService(
    GitHubService gitHubService,
    ILogger<GitHubPullRequestMergeService> logger)
{
    private static readonly MergeMethod[] DefaultMethodPriority = [MergeMethod.Squash, MergeMethod.Merge, MergeMethod.Rebase];

    /// <summary>Fetches everything the merge dialog needs in one call (used by the periodic silent in-dialog refresh, which has no spinner to split around). Returns null when the repository/connector is not GitHub-backed or the PR cannot be found.</summary>
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
        var snapshot = await GetPullRequestSnapshotAsync(repository, connector, prNumber, cancellationToken);
        if (snapshot == null)
            return null;

        var review = await GetMergeReviewDetailsInternalAsync(repository, connector, prNumber, snapshot.HeadSha, cancellationToken);
        if (review == null)
            return null;

        return new PullRequestMergeDetails
        {
            Number = snapshot.Number,
            Title = snapshot.Title,
            HeadRef = snapshot.HeadRef,
            BaseRef = snapshot.BaseRef,
            HeadSha = snapshot.HeadSha,
            HtmlUrl = snapshot.HtmlUrl,
            ChangedFiles = snapshot.ChangedFiles,
            Mergeable = snapshot.Mergeable,
            MergeableState = snapshot.MergeableState,
            ApprovedCount = review.ApprovedCount,
            ChangesRequestedCount = review.ChangesRequestedCount,
            OutstandingReviewers = review.OutstandingReviewers,
            ApprovedByUsers = review.ApprovedByUsers,
            Checks = review.Checks,
            AllowedMergeMethods = review.AllowedMergeMethods,
            DefaultMergeMethod = review.DefaultMergeMethod,
            HasUncommittedChanges = hasUncommittedChanges,
            UncommittedChangesCount = uncommittedChangesCount,
            UnpushedCommitsCount = unpushedCommitsCount,
            IncomingCommitsCount = incomingCommitsCount
        };
    }

    /// <summary>
    /// Fast phase: just the (ETag-conditional) PR-by-number call, returning title/branches/conflicts - everything
    /// the dialog's header, title/branches row, and Conflicts row need. Returns null when the repository/connector
    /// is not GitHub-backed or the PR cannot be found.
    /// </summary>
    public async Task<PullRequestMergeSnapshot?> GetPullRequestSnapshotAsync(
        Repository repository,
        Connector? connector,
        int prNumber,
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

        return new PullRequestMergeSnapshot(
            pr.Number,
            pr.Title ?? string.Empty,
            pr.Head?.Ref ?? string.Empty,
            pr.Base?.Ref ?? string.Empty,
            pr.Head?.Sha,
            pr.HtmlUrl ?? string.Empty,
            pr.ChangedFiles,
            pr.Mergeable,
            pr.MergeableState);
    }

    /// <summary>
    /// Background phase: the reviews/repo-settings/checks fan-out (all now ETag-conditional), returning approvals/
    /// outstanding reviewers/checks summary/allowed merge methods. Requires <paramref name="headSha"/> from a prior
    /// <see cref="GetPullRequestSnapshotAsync"/> call for the checks lookup. Returns null only when the repository/
    /// connector is not GitHub-backed (mirrors the snapshot phase's own guard).
    /// </summary>
    public async Task<PullRequestMergeReviewData?> GetMergeReviewDetailsAsync(
        Repository repository,
        Connector? connector,
        int prNumber,
        string? headSha,
        CancellationToken cancellationToken = default) =>
        await GetMergeReviewDetailsInternalAsync(repository, connector, prNumber, headSha, cancellationToken);

    private async Task<PullRequestMergeReviewData?> GetMergeReviewDetailsInternalAsync(
        Repository? repository,
        Connector? connector,
        int prNumber,
        string? headSha,
        CancellationToken cancellationToken)
    {
        if (repository == null || prNumber <= 0)
            return null;
        if (connector == null || connector.ConnectorType != ConnectorType.GitHub || string.IsNullOrWhiteSpace(connector.UserToken))
            return null;
        if (!RepositoryUrlHelper.TryParseGitHubOwnerRepo(repository.CloneUrl, out var owner, out var repo) || owner == null || repo == null)
            return null;

        // RequestedReviewers isn't returned by the reviews endpoint, only by the PR-by-number payload - re-fetched
        // here (ETag-cheap, usually a 304 right after the snapshot phase already pulled the same PR) rather than
        // threading it through as an extra parameter.
        var prTask = gitHubService.GetPullRequestByNumberAsync(connector, owner, repo, prNumber, cancellationToken);
        var reviewsTask = gitHubService.GetPullRequestReviewsAsync(connector, owner, repo, prNumber, cancellationToken);
        var settingsTask = gitHubService.GetRepositoryMergeSettingsAsync(connector, owner, repo, cancellationToken);
        var checksTask = string.IsNullOrWhiteSpace(headSha)
            ? Task.FromResult<GitHubCheckRunsResponse?>(null)
            : gitHubService.GetCheckRunsForRefAsync(connector, owner, repo, headSha, cancellationToken);

        await Task.WhenAll(prTask, reviewsTask, settingsTask, checksTask);

        var pr = prTask.Result;
        var reviews = reviewsTask.Result;
        var settings = settingsTask.Result;
        var checkRuns = checksTask.Result;

        var (approvedCount, changesRequestedCount, outstandingReviewers, approvedByUsers) = SummarizeReviews(reviews, pr?.RequestedReviewers, pr?.RequestedTeams);
        var checksSummary = SummarizeChecks(checkRuns);
        var allowedMethods = GetAllowedMergeMethods(settings);
        var defaultMethod = DefaultMethodPriority.FirstOrDefault(m => allowedMethods.Contains(m), allowedMethods.FirstOrDefault());

        return new PullRequestMergeReviewData(
            approvedCount,
            changesRequestedCount,
            outstandingReviewers,
            approvedByUsers,
            checksSummary,
            allowedMethods,
            allowedMethods.Count > 0 ? defaultMethod : null);
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
        List<GitHubUserDto>? requestedReviewers,
        List<GitHubTeamDto>? requestedTeams)
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
            .Concat((requestedTeams ?? [])
                .Select(t => t.Slug)
                .Where(s => !string.IsNullOrWhiteSpace(s)))
            .ToList();

        return (approvedByUsers.Count, changesRequested, outstanding, approvedByUsers);
    }

    private static ChecksSummary SummarizeChecks(GitHubCheckRunsResponse? response)
    {
        var runs = response?.CheckRuns ?? [];
        if (runs.Count == 0)
            return new ChecksSummary { State = ChecksState.None };

        // The check-runs API returns every attempt; GitHub's merge box keeps the latest run per check name.
        var latestByName = runs
            .GroupBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => g
                .OrderBy(r => r.CompletedAt ?? r.StartedAt ?? DateTimeOffset.MinValue)
                .ThenBy(r => r.Id)
                .Last())
            .ToList();

        var pending = latestByName.Count(r => !string.Equals(r.Status, "completed", StringComparison.OrdinalIgnoreCase));
        var failed = latestByName.Count(r => string.Equals(r.Status, "completed", StringComparison.OrdinalIgnoreCase)
            && r.Conclusion is not null
            && !string.Equals(r.Conclusion, "success", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(r.Conclusion, "neutral", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(r.Conclusion, "skipped", StringComparison.OrdinalIgnoreCase));
        var passed = latestByName.Count - pending - failed;

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
