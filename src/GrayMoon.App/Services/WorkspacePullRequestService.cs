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
    private int MaxConcurrency => Math.Max(1, workspaceOptions.Value.MaxParallelOperations);

    /// <summary>Returns persisted PR state for the workspace keyed by RepositoryId. Used when building grid from cache.</summary>
    public async Task<IReadOnlyDictionary<int, PullRequestInfo?>> GetPersistedPullRequestsForWorkspaceAsync(int workspaceId, CancellationToken cancellationToken = default)
    {
        return await pullRequestRepository.GetByWorkspaceIdAsync(workspaceId, cancellationToken);
    }

    /// <summary>Fetches PR from API for the given repos and persists. Call after sync, refresh, push, or hooks.</summary>
    public async Task RefreshPullRequestsAsync(int workspaceId, IReadOnlyList<int> repositoryIds, CancellationToken cancellationToken = default)
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
        var tasks = toRefresh.Select(async wr =>
        {
            await semaphore.WaitAsync(cancellationToken);
            try
            {
                var pr = await gitHubPullRequestService.GetPullRequestForBranchAsync(wr.Repository!, wr.Repository!.Connector, wr.BranchName, cancellationToken);
                await pullRequestRepository.UpsertAsync(wr.WorkspaceRepositoryId, pr, cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "RefreshPullRequest failed. WorkspaceId={WorkspaceId}, RepositoryId={RepositoryId}", workspaceId, wr.RepositoryId);
            }
            finally
            {
                semaphore.Release();
            }
        });
        await Task.WhenAll(tasks);
        logger.LogTrace("Refreshed PR for {Count} repo(s) in workspace {WorkspaceId}", toRefresh.Count, workspaceId);
    }

    /// <summary>Refreshes PR for all repositories in the workspace.</summary>
    public async Task RefreshPullRequestsForWorkspaceAsync(int workspaceId, CancellationToken cancellationToken = default)
    {
        var repoIds = await dbContext.WorkspaceRepositories
            .Where(wr => wr.WorkspaceId == workspaceId)
            .Select(wr => wr.RepositoryId)
            .ToListAsync(cancellationToken);
        await RefreshPullRequestsAsync(workspaceId, repoIds, cancellationToken);
    }
}
