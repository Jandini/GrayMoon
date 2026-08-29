using System.Collections.Concurrent;
using GrayMoon.App.Data;
using GrayMoon.App.Models;
using GrayMoon.App.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GrayMoon.App.Services;

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

/// <summary>Single service for PR persistence and refresh. Fetches via GitHub API and persists via WorkspacePullRequestRepository.</summary>
public sealed class WorkspacePullRequestService(
    WorkspacePullRequestRepository pullRequestRepository,
    GitHubPullRequestService gitHubPullRequestService,
    AppDbContext dbContext,
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

        var links = await dbContext.WorkspaceRepositories
            .AsNoTracking()
            .Include(wr => wr.Repository)
            .ThenInclude(r => r!.Connector)
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

    /// <summary>Closes an open pull request for the given repository. Looks up the connector from the workspace link and calls GitHub API. Logs and returns silently on error.</summary>
    public async Task ClosePullRequestAsync(int workspaceId, int repositoryId, int prNumber, CancellationToken cancellationToken = default)
    {
        if (prNumber <= 0) return;

        var link = await dbContext.WorkspaceRepositories
            .AsNoTracking()
            .Include(wr => wr.Repository)
            .ThenInclude(r => r!.Connector)
            .FirstOrDefaultAsync(wr => wr.WorkspaceId == workspaceId && wr.RepositoryId == repositoryId, cancellationToken);

        if (link?.Repository == null)
            return;

        await gitHubPullRequestService.ClosePullRequestAsync(link.Repository, link.Repository.Connector, prNumber, cancellationToken);
    }

    /// <summary>Refreshes PR for all repositories in the workspace.</summary>
    public async Task RefreshPullRequestsForWorkspaceAsync(int workspaceId, CancellationToken cancellationToken = default)
    {
        var repoIds = await dbContext.WorkspaceRepositories
            .Where(wr => wr.WorkspaceId == workspaceId)
            .Select(wr => wr.RepositoryId)
            .ToListAsync(cancellationToken);
        await RefreshPullRequestsAsync(workspaceId, repoIds, cancellationToken: cancellationToken);
    }
}
