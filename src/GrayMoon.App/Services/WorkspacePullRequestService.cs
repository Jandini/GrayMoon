using System.Collections.Concurrent;
using GrayMoon.App.Data;
using GrayMoon.App.Models;
using GrayMoon.App.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GrayMoon.App.Services;

/// <summary>Single service for PR persistence and refresh. Fetches via GitHub API and persists via WorkspacePullRequestRepository.</summary>
public sealed class WorkspacePullRequestService(
    WorkspacePullRequestRepository pullRequestRepository,
    GitHubPullRequestService gitHubPullRequestService,
    AppDbContext dbContext,
    IOptions<WorkspaceOptions> workspaceOptions,
    ILogger<WorkspacePullRequestService> logger)
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(60);
    private readonly ConcurrentDictionary<(int RepoId, string Branch), (PullRequestInfo? Result, DateTime FetchedAt)> _cache = new();

    private int MaxConcurrency => Math.Max(1, workspaceOptions.Value.MaxParallelOperations);

    /// <summary>Returns persisted PR state for the workspace keyed by RepositoryId. Used when building grid from cache.</summary>
    public async Task<IReadOnlyDictionary<int, PullRequestInfo?>> GetPersistedPullRequestsForWorkspaceAsync(int workspaceId, CancellationToken cancellationToken = default)
    {
        return await pullRequestRepository.GetByWorkspaceIdAsync(workspaceId, cancellationToken);
    }

    /// <summary>Fetches PR from API for the given repos and persists. Call after sync, refresh, push, or hooks.</summary>
    public async Task RefreshPullRequestsAsync(int workspaceId, IReadOnlyList<int> repositoryIds, bool force = false, CancellationToken cancellationToken = default)
    {
        if (repositoryIds.Count == 0) return;

        var links = await dbContext.WorkspaceRepositories
            .AsNoTracking()
            .Include(wr => wr.Repository)
            .ThenInclude(r => r!.Connector)
            .Where(wr => wr.WorkspaceId == workspaceId && repositoryIds.Contains(wr.RepositoryId))
            .ToListAsync(cancellationToken);

        var toRefresh = links.Where(wr => wr.Repository != null && !string.IsNullOrWhiteSpace(wr.BranchName)).ToList();
        if (toRefresh.Count == 0) return;

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
                    return (Wr: wr, Pr: (PullRequestInfo?)null, Skip: true);
                }

                var pr = await gitHubPullRequestService.GetPullRequestForBranchAsync(wr.Repository!, wr.Repository!.Connector, branch, cancellationToken);
                _cache[cacheKey] = (pr, DateTime.UtcNow);
                return (Wr: wr, Pr: pr, Skip: false);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "RefreshPullRequest failed. WorkspaceId={WorkspaceId}, RepositoryId={RepositoryId}", workspaceId, wr.RepositoryId);
                return (Wr: wr, Pr: (PullRequestInfo?)null, Skip: true);
            }
            finally
            {
                semaphore.Release();
            }
        });
        var fetched = await Task.WhenAll(fetchTasks);

        foreach (var result in fetched.Where(r => !r.Skip))
            await pullRequestRepository.UpsertAsync(result.Wr.WorkspaceRepositoryId, result.Pr, cancellationToken);

        logger.LogTrace("Refreshed PR for {Count} repo(s) in workspace {WorkspaceId}", toRefresh.Count, workspaceId);
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
