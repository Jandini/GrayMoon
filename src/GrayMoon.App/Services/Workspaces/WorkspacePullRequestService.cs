using System.Collections.Concurrent;
using GrayMoon.App.Data;
using GrayMoon.App.Models;
using GrayMoon.App.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GrayMoon.App.Services.Workspaces;

/// <summary>What happened when a repository's pull request state was refreshed.</summary>
public enum PullRequestRefreshOutcome
{
    /// <summary>GitHub answered and the row was rewritten.</summary>
    Refreshed,

    /// <summary>A recent lookup for the same branch was reused; the row is unchanged and still current.</summary>
    CacheHit,

    /// <summary>The repository cannot have a pull request (no branch checked out) and the row was cleared.</summary>
    Cleared,

    /// <summary>The lookup threw. The persisted row is stale and the caller should treat the PR state as unknown.</summary>
    Failed,
}

/// <summary>Review-details phase result (<see cref="GitHubPullRequestMergeService.GetMergeReviewDetailsAsync"/>) plus the workspace's own persisted Git Changes projection (uncommitted changes / unpushed / incoming commits) - a GrayMoon-local, informational-only signal that never blocks the merge itself.</summary>
/// <summary>Local-only git state for one repository - see <see cref="WorkspacePullRequestService.GetLocalGitStateAsync"/>.</summary>
public readonly record struct LocalGitState(bool HasLocalWarning, int UncommittedChangesCount, int UnpushedCommitsCount, int IncomingCommitsCount);

public sealed record PullRequestMergeReviewDetails(
    int ApprovedCount,
    int ChangesRequestedCount,
    IReadOnlyList<string> OutstandingReviewers,
    IReadOnlyList<string> ApprovedByUsers,
    bool HasReviewerComments,
    ChecksSummary Checks,
    IReadOnlyList<MergeMethod> AllowedMergeMethods,
    MergeMethod? DefaultMergeMethod,
    bool HasUncommittedChanges,
    int UncommittedChangesCount,
    int UnpushedCommitsCount,
    int IncomingCommitsCount);

/// <summary>Single service for PR persistence and refresh. Fetches via GitHub API and persists via WorkspacePullRequestRepository.</summary>
public sealed class WorkspacePullRequestService(
    WorkspacePullRequestRepository pullRequestRepository,
    GitHubPullRequestService gitHubPullRequestService,
    GitHubPullRequestMergeService gitHubPullRequestMergeService,
    IDbContextFactory<AppDbContext> dbContextFactory,
    IOptions<WorkspaceOptions> workspaceOptions,
    IGitHubRateLimitTracker rateLimitTracker,
    ILogger<WorkspacePullRequestService> logger)
{
    // Kept just under the 5s background poll interval (WorkspaceRepositories.PrPolling.cs) so every poll tick
    // reaches the ETag-backed GitHub call (usually a free 304) instead of being served entirely from this
    // app-level cache. The cache still absorbs near-simultaneous duplicate calls (multiple tabs, poll racing a
    // forced action) within that short window.
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(3);
    private readonly ConcurrentDictionary<(int RepoId, string Branch), (PullRequestInfo? Result, DateTime FetchedAt)> _cache = new();

    private int MaxConcurrency => Math.Max(1, workspaceOptions.Value.MaxParallelOperations);

    /// <summary>Returns persisted PR state for the workspace keyed by RepositoryId. Used when building grid from cache.</summary>
    public async Task<IReadOnlyDictionary<int, PullRequestInfo?>> GetPersistedPullRequestsForWorkspaceAsync(int workspaceId, CancellationToken cancellationToken = default)
    {
        return await pullRequestRepository.GetByWorkspaceIdAsync(workspaceId, cancellationToken);
    }

    /// <summary>
    /// Fetches PR state from the API for the given repos and persists it. Call after sync, refresh, push, or hooks.
    /// Returns the outcome per repository so callers can tell "there is no PR" apart from "we could not find out".
    /// </summary>
    public async Task<IReadOnlyDictionary<int, PullRequestRefreshOutcome>> RefreshPullRequestsAsync(
        int workspaceId,
        IReadOnlyList<int> repositoryIds,
        bool force = false,
        CancellationToken cancellationToken = default)
    {
        var outcomes = new Dictionary<int, PullRequestRefreshOutcome>();
        if (repositoryIds.Count == 0) return outcomes;

        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var links = await dbContext.WorkspaceRepositories
            .AsNoTracking()
            .Include(wr => wr.Repository)
            .ThenInclude(r => r!.Connector)
            .Include(wr => wr.PullRequest)
            .Where(wr => wr.WorkspaceId == workspaceId && repositoryIds.Contains(wr.RepositoryId))
            .ToListAsync(cancellationToken);

        // A repository with no branch checked out (or pinned to a tag) cannot have a pull request, so
        // clear the row rather than skipping it and leaving the previous branch's badge on screen.
        var toClear = links.Where(wr => wr.Repository == null || string.IsNullOrWhiteSpace(wr.BranchName)).ToList();
        foreach (var wr in toClear)
        {
            await pullRequestRepository.UpsertAsync(wr.WorkspaceRepositoryId, null, cancellationToken);
            outcomes[wr.RepositoryId] = PullRequestRefreshOutcome.Cleared;
        }

        var toRefresh = links.Where(wr => wr.Repository != null && !string.IsNullOrWhiteSpace(wr.BranchName)).ToList();
        if (toRefresh.Count == 0) return outcomes;

        using var semaphore = new SemaphoreSlim(MaxConcurrency, MaxConcurrency);
        var fetchTasks = toRefresh.Select(async wr =>
        {
            await semaphore.WaitAsync(cancellationToken);
            try
            {
                var branch = wr.BranchName!;
                var cacheKey = (wr.RepositoryId, branch);
                if (!force && _cache.TryGetValue(cacheKey, out var cached) && DateTime.UtcNow - cached.FetchedAt < CacheTtl)
                {
                    logger.LogTrace("PR cache hit for repo {RepositoryId}, branch {Branch}", wr.RepositoryId, branch);
                    return (Wr: wr, Pr: (PullRequestInfo?)null, Outcome: PullRequestRefreshOutcome.CacheHit);
                }

                // Skip the call entirely while the connector is paused for rate-limit backoff (shared across
                // every poller for that connector via IGitHubRateLimitTracker), so background polling and
                // bursts of forced refreshes cannot pile on top of an already-exhausted connector.
                var connectorName = wr.Repository!.Connector?.ConnectorName;
                if (!string.IsNullOrWhiteSpace(connectorName) && rateLimitTracker.GetPausedUntil(connectorName) is { } pausedUntil)
                {
                    logger.LogTrace("PR refresh skipped (rate-limited until {PausedUntil}) for repo {RepositoryId}", pausedUntil, wr.RepositoryId);
                    return (Wr: wr, Pr: (PullRequestInfo?)null, Outcome: PullRequestRefreshOutcome.Failed);
                }

                var pr = await gitHubPullRequestService.GetPullRequestForBranchAsync(wr.Repository!, wr.Repository!.Connector, branch, cancellationToken);
                // After GitHub auto-deletes the head branch on merge, the list-by-head lookup is empty even
                // though GET /pulls/{number} still returns the merged PR. Fall back so post-merge
                // return-to-default still sees MergedAt and does not abort as "not safe".
                if (pr == null && wr.PullRequest?.PullRequestNumber is int persistedNumber and > 0)
                {
                    pr = await gitHubPullRequestService.GetPullRequestByNumberAsync(
                        wr.Repository!, wr.Repository!.Connector, persistedNumber, branch, cancellationToken);
                }
                _cache[cacheKey] = (pr, DateTime.UtcNow);
                return (Wr: wr, Pr: pr, Outcome: PullRequestRefreshOutcome.Refreshed);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "RefreshPullRequest failed. WorkspaceId={WorkspaceId}, RepositoryId={RepositoryId}", workspaceId, wr.RepositoryId);
                return (Wr: wr, Pr: (PullRequestInfo?)null, Outcome: PullRequestRefreshOutcome.Failed);
            }
            finally
            {
                semaphore.Release();
            }
        });
        var fetched = await Task.WhenAll(fetchTasks);

        foreach (var result in fetched)
        {
            outcomes[result.Wr.RepositoryId] = result.Outcome;
            if (result.Outcome == PullRequestRefreshOutcome.Refreshed)
                await pullRequestRepository.UpsertAsync(result.Wr.WorkspaceRepositoryId, result.Pr, cancellationToken);
        }

        logger.LogTrace("Refreshed PR for {Count} repo(s) in workspace {WorkspaceId}", toRefresh.Count, workspaceId);
        return outcomes;
    }

    /// <summary>Clears the persisted pull request for a repository without contacting GitHub. Used when the checked-out branch cannot have one (default branch, tag, or no branch at all).</summary>
    public async Task ClearPullRequestAsync(int workspaceId, int repositoryId, CancellationToken cancellationToken = default)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var workspaceRepositoryId = await dbContext.WorkspaceRepositories
            .AsNoTracking()
            .Where(wr => wr.WorkspaceId == workspaceId && wr.RepositoryId == repositoryId)
            .Select(wr => wr.WorkspaceRepositoryId)
            .FirstOrDefaultAsync(cancellationToken);
        if (workspaceRepositoryId == 0)
            return;

        await pullRequestRepository.UpsertAsync(workspaceRepositoryId, null, cancellationToken);
    }

    /// <summary>Drops every cached PR lookup for a repository. Call on branch change so the old branch's entry cannot be served after a later checkout back onto it.</summary>
    public void EvictCacheForRepository(int repositoryId)
    {
        foreach (var key in _cache.Keys.Where(k => k.RepoId == repositoryId).ToList())
            _cache.TryRemove(key, out _);
    }

    /// <summary>Closes an open pull request via GitHub without merging and, on success, forces an immediate PR refresh so the grid badge drops the open-PR chip without waiting for the next poll.</summary>
    public async Task<MergeResult> ClosePullRequestAsync(int workspaceId, int repositoryId, int prNumber, CancellationToken cancellationToken = default)
    {
        if (prNumber <= 0)
            return new MergeResult(false, "Invalid pull request.");

        var link = await GetLinkWithConnectorAsync(workspaceId, repositoryId, cancellationToken);
        if (link?.Repository == null)
            return new MergeResult(false, "Repository not found in this workspace.");

        var result = await gitHubPullRequestMergeService.ClosePullRequestAsync(
            link.Repository,
            link.Repository.Connector,
            prNumber,
            cancellationToken);
        if (result.Success)
        {
            _cache.TryRemove((repositoryId, link.BranchName ?? string.Empty), out _);
            await RefreshPullRequestsAsync(workspaceId, [repositoryId], force: true, cancellationToken: cancellationToken);
        }
        return result;
    }

    /// <summary>Refreshes PR for all repositories in the workspace.</summary>
    public async Task RefreshPullRequestsForWorkspaceAsync(int workspaceId, CancellationToken cancellationToken = default)
    {
        List<int> repoIds;
        await using (var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken))
        {
            repoIds = await dbContext.WorkspaceRepositories
                .Where(wr => wr.WorkspaceId == workspaceId)
                .Select(wr => wr.RepositoryId)
                .ToListAsync(cancellationToken);
        }
        await RefreshPullRequestsAsync(workspaceId, repoIds, cancellationToken: cancellationToken);
    }

    /// <summary>Fetches a fresh, on-demand mergeability snapshot (PR, reviews, checks, repo merge settings) for the merge confirmation dialog's periodic silent refresh (no spinner to split around, so one combined call is simplest). Always hits GitHub live (ETag-conditional) - never served from the polled PR cache. Also folds in the workspace's own persisted Git Changes projection (uncommitted changes / unpushed commits), a GrayMoon-local, informational-only signal that never blocks the merge itself.</summary>
    public async Task<PullRequestMergeDetails?> GetMergeDetailsAsync(int workspaceId, int repositoryId, int prNumber, CancellationToken cancellationToken = default)
    {
        var link = await GetLinkWithConnectorAndGitStatusAsync(workspaceId, repositoryId, cancellationToken);
        if (link?.Repository == null)
            return null;

        var (hasUncommitted, uncommittedCount, unpushedCount, incomingCount) = ComputeLocalGitState(link);

        return await gitHubPullRequestMergeService.GetMergeDetailsAsync(
            link.Repository,
            link.Repository.Connector,
            prNumber,
            hasUncommittedChanges: hasUncommitted,
            uncommittedChangesCount: uncommittedCount,
            unpushedCommitsCount: unpushedCount,
            incomingCommitsCount: incomingCount,
            cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Local-only git state (uncommitted changes / unpushed / incoming commits) for one repository - reads the
    /// already-persisted Git Changes projection, no GitHub call. Used by the bulk-merge row list alongside
    /// <see cref="GetMergeSnapshotAsync"/> so a row never shows "ready to merge" green while the local clone still
    /// has uncommitted work, without paying for the heavier per-PR review-details GitHub round trip.
    /// </summary>
    public async Task<LocalGitState> GetLocalGitStateAsync(int workspaceId, int repositoryId, CancellationToken cancellationToken = default)
    {
        var link = await GetLinkWithConnectorAndGitStatusAsync(workspaceId, repositoryId, cancellationToken);
        if (link == null)
            return default;

        var (hasWarning, uncommittedCount, unpushedCount, incomingCount) = ComputeLocalGitState(link);
        return new LocalGitState(hasWarning, uncommittedCount, unpushedCount, incomingCount);
    }

    /// <summary>
    /// Fast phase for opening the merge dialog: just the (ETag-conditional) PR-by-number call, returning title/
    /// branches/conflicts so the dialog can render its header and Conflicts row before the heavier review-details
    /// phase below even starts.
    /// </summary>
    public async Task<PullRequestMergeSnapshot?> GetMergeSnapshotAsync(int workspaceId, int repositoryId, int prNumber, CancellationToken cancellationToken = default)
    {
        var link = await GetLinkWithConnectorAsync(workspaceId, repositoryId, cancellationToken);
        if (link?.Repository == null)
            return null;

        return await gitHubPullRequestMergeService.GetPullRequestSnapshotAsync(link.Repository, link.Repository.Connector, prNumber, cancellationToken);
    }

    /// <summary>
    /// Background phase for opening the merge dialog: reviews/checks/allowed-methods (all now ETag-conditional),
    /// plus the workspace's own persisted local git state for the local-state row. Runs after
    /// <see cref="GetMergeSnapshotAsync"/> has already let the dialog render its title/branches/conflicts.
    /// </summary>
    public async Task<PullRequestMergeReviewDetails?> GetMergeReviewDetailsAsync(int workspaceId, int repositoryId, int prNumber, string? headSha, CancellationToken cancellationToken = default)
    {
        var link = await GetLinkWithConnectorAndGitStatusAsync(workspaceId, repositoryId, cancellationToken);
        if (link?.Repository == null)
            return null;

        var review = await gitHubPullRequestMergeService.GetMergeReviewDetailsAsync(link.Repository, link.Repository.Connector, prNumber, headSha, cancellationToken);
        if (review == null)
            return null;

        var (hasUncommitted, uncommittedCount, unpushedCount, incomingCount) = ComputeLocalGitState(link);

        return new PullRequestMergeReviewDetails(
            review.ApprovedCount,
            review.ChangesRequestedCount,
            review.OutstandingReviewers,
            review.ApprovedByUsers,
            review.HasReviewerComments,
            review.Checks,
            review.AllowedMergeMethods,
            review.DefaultMergeMethod,
            hasUncommitted,
            uncommittedCount,
            unpushedCount,
            incomingCount);
    }

    private static (bool HasUncommittedChanges, int UncommittedChangesCount, int UnpushedCommitsCount, int IncomingCommitsCount) ComputeLocalGitState(WorkspaceRepositoryLink link)
    {
        var uncommittedChangesCount = (link.GitStatus?.StagedCount ?? 0) + (link.GitStatus?.ChangedCount ?? 0);
        var unpushedCommitsCount = link.OutgoingCommits ?? 0;
        var incomingCommitsCount = link.BranchHasUpstream == false ? 0 : link.IncomingCommits ?? 0;
        return (uncommittedChangesCount > 0, uncommittedChangesCount, unpushedCommitsCount, incomingCommitsCount);
    }

    private async Task<WorkspaceRepositoryLink?> GetLinkWithConnectorAndGitStatusAsync(int workspaceId, int repositoryId, CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await dbContext.WorkspaceRepositories
            .AsNoTracking()
            .Include(wr => wr.Repository)
            .ThenInclude(r => r!.Connector)
            .Include(wr => wr.GitStatus)
            .FirstOrDefaultAsync(wr => wr.WorkspaceId == workspaceId && wr.RepositoryId == repositoryId, cancellationToken);
    }

    /// <summary>Updates only the pull request title on GitHub. Leaves body, state, and base branch unchanged.</summary>
    public async Task<MergeResult> UpdatePullRequestTitleAsync(int workspaceId, int repositoryId, int prNumber, string title, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(title))
            return new MergeResult(false, "Title is required.");

        var link = await GetLinkWithConnectorAsync(workspaceId, repositoryId, cancellationToken);
        if (link?.Repository == null)
            return new MergeResult(false, "Repository not found in this workspace.");

        return await gitHubPullRequestMergeService.UpdatePullRequestTitleAsync(
            link.Repository,
            link.Repository.Connector,
            prNumber,
            title.Trim(),
            cancellationToken);
    }

    /// <summary>Merges the pull request via GitHub and, on success, forces an immediate PR refresh so the grid badge flips to "merged" without waiting for the next poll.</summary>
    public async Task<MergeResult> MergePullRequestAsync(int workspaceId, int repositoryId, int prNumber, MergeMethod method, string? expectedHeadSha, CancellationToken cancellationToken = default)
    {
        var link = await GetLinkWithConnectorAsync(workspaceId, repositoryId, cancellationToken);
        if (link?.Repository == null)
            return new MergeResult(false, "Repository not found in this workspace.");

        var result = await gitHubPullRequestMergeService.MergePullRequestAsync(link.Repository, link.Repository.Connector, prNumber, method, expectedHeadSha, cancellationToken);
        if (result.Success)
        {
            await PersistMergedAsync(link.WorkspaceRepositoryId, prNumber, cancellationToken);
            _cache.TryRemove((repositoryId, link.BranchName ?? string.Empty), out _);
            await RefreshPullRequestsAsync(workspaceId, [repositoryId], force: true, cancellationToken: cancellationToken);
        }
        return result;
    }

    private async Task<WorkspaceRepositoryLink?> GetLinkWithConnectorAsync(int workspaceId, int repositoryId, CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await dbContext.WorkspaceRepositories
            .AsNoTracking()
            .Include(wr => wr.Repository)
            .ThenInclude(r => r!.Connector)
            .FirstOrDefaultAsync(wr => wr.WorkspaceId == workspaceId && wr.RepositoryId == repositoryId, cancellationToken);
    }

    /// <summary>
    /// Merges many pull requests across a workspace in one bounded-concurrency batch (the plural counterpart to
    /// <see cref="MergePullRequestAsync"/>). A null <see cref="MergePullRequestRequest.Method"/> resolves the
    /// repository's own GitHub-reported default merge method lazily, per request, rather than up front for every
    /// candidate. On success, refreshes PR state once for the whole successfully-merged set.
    /// </summary>
    public async Task<IReadOnlyList<MergePullRequestResult>> MergePullRequestsAsync(
        int workspaceId,
        IReadOnlyList<MergePullRequestRequest> requests,
        IProgress<MergePullRequestProgress>? progress,
        CancellationToken cancellationToken = default)
    {
        if (requests.Count == 0) return Array.Empty<MergePullRequestResult>();

        var repositoryIds = requests.Select(r => r.RepositoryId).Distinct().ToList();
        List<WorkspaceRepositoryLink> links;
        await using (var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken))
        {
            links = await dbContext.WorkspaceRepositories
                .AsNoTracking()
                .Include(wr => wr.Repository)
                .ThenInclude(r => r!.Connector)
                .Where(wr => wr.WorkspaceId == workspaceId && repositoryIds.Contains(wr.RepositoryId))
                .ToListAsync(cancellationToken);
        }
        var linkByRepoId = links.ToDictionary(wr => wr.RepositoryId);

        var total = requests.Count;
        var completed = 0;
        var failed = 0;

        using var semaphore = new SemaphoreSlim(MaxConcurrency, MaxConcurrency);
        var tasks = requests.Select(async request =>
        {
            await semaphore.WaitAsync(cancellationToken);
            try
            {
                var result = await MergeOneAsync(request, linkByRepoId, cancellationToken);

                var completedCount = Interlocked.Increment(ref completed);
                var failedCount = result.Success ? Volatile.Read(ref failed) : Interlocked.Increment(ref failed);
                progress?.Report(new MergePullRequestProgress
                {
                    Completed = completedCount,
                    Failed = failedCount,
                    Total = total,
                    CurrentRepositoryId = result.RepositoryId,
                    CurrentSuccess = result.Success,
                    CurrentErrorMessage = result.ErrorMessage
                });

                return result;
            }
            finally
            {
                semaphore.Release();
            }
        });

        var results = await Task.WhenAll(tasks);

        var successfulRepoIds = results.Where(r => r.Success).Select(r => r.RepositoryId).Distinct().ToList();
        foreach (var repoId in successfulRepoIds)
        {
            if (linkByRepoId.TryGetValue(repoId, out var link))
                _cache.TryRemove((repoId, link.BranchName ?? string.Empty), out _);
        }

        if (successfulRepoIds.Count > 0)
            await RefreshPullRequestsAsync(workspaceId, successfulRepoIds, force: true, cancellationToken: cancellationToken);

        return results;
    }

    private async Task<MergePullRequestResult> MergeOneAsync(
        MergePullRequestRequest request,
        IReadOnlyDictionary<int, WorkspaceRepositoryLink> linkByRepoId,
        CancellationToken cancellationToken)
    {
        if (!linkByRepoId.TryGetValue(request.RepositoryId, out var link) || link.Repository == null)
            return FailedMerge(request, "Repository not found in this workspace.");

        var connectorName = link.Repository.Connector?.ConnectorName;
        if (!string.IsNullOrWhiteSpace(connectorName) && rateLimitTracker.GetPausedUntil(connectorName) is { } pausedUntil)
            return FailedMerge(request, $"GitHub rate limit - paused until {pausedUntil.UtcDateTime:HH:mm} UTC.");

        var method = request.Method
            ?? await ResolveDefaultMergeMethodAsync(link.Repository, link.Repository.Connector, request, cancellationToken);

        var result = await gitHubPullRequestMergeService.MergePullRequestAsync(
            link.Repository, link.Repository.Connector, request.PrNumber, method, request.ExpectedHeadSha, cancellationToken);

        if (result.Success)
            await PersistMergedAsync(link.WorkspaceRepositoryId, request.PrNumber, cancellationToken);

        return new MergePullRequestResult
        {
            RepositoryId = request.RepositoryId,
            PrNumber = request.PrNumber,
            Success = result.Success,
            ErrorMessage = result.Success ? null : result.Message
        };
    }

    private async Task<MergeMethod> ResolveDefaultMergeMethodAsync(
        Repository repository,
        Connector? connector,
        MergePullRequestRequest request,
        CancellationToken cancellationToken)
    {
        var review = await gitHubPullRequestMergeService.GetMergeReviewDetailsAsync(
            repository, connector, request.PrNumber, request.ExpectedHeadSha, cancellationToken);
        return review?.DefaultMergeMethod ?? review?.AllowedMergeMethods.FirstOrDefault() ?? MergeMethod.Merge;
    }

    private static MergePullRequestResult FailedMerge(MergePullRequestRequest request, string message) => new()
    {
        RepositoryId = request.RepositoryId,
        PrNumber = request.PrNumber,
        Success = false,
        ErrorMessage = message
    };

    /// <summary>
    /// Writes merged/closed locally before the post-merge GitHub refresh so a list-by-head miss (GitHub already
    /// deleted the branch) still has a PR number to fall back on.
    /// </summary>
    private Task PersistMergedAsync(int workspaceRepositoryId, int prNumber, CancellationToken cancellationToken) =>
        pullRequestRepository.UpsertAsync(workspaceRepositoryId, new PullRequestInfo
        {
            Number = prNumber,
            State = "closed",
            MergedAt = DateTimeOffset.UtcNow
        }, cancellationToken);
}
