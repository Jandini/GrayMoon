using GrayMoon.App.Data;
using GrayMoon.App.Models;
using Microsoft.EntityFrameworkCore;

namespace GrayMoon.App.Repositories;

/// <summary>Persistence for pull request state per workspace–repository link. Single place for all PR table read/write.</summary>
public sealed class WorkspacePullRequestRepository(AppDbContext dbContext, ILogger<WorkspacePullRequestRepository> logger)
{
    /// <summary>Returns persisted PR state for all repositories in the workspace, keyed by RepositoryId. Missing row yields null (no PR or not yet checked).</summary>
    public async Task<IReadOnlyDictionary<int, PullRequestInfo?>> GetByWorkspaceIdAsync(int workspaceId, CancellationToken cancellationToken = default)
    {
        var links = await dbContext.WorkspaceRepositories
            .AsNoTracking()
            .Include(wr => wr.PullRequest)
            .Where(wr => wr.WorkspaceId == workspaceId)
            .ToListAsync(cancellationToken);

        var result = new Dictionary<int, PullRequestInfo?>();
        foreach (var link in links)
        {
            if (link.PullRequest == null) continue;
            result[link.RepositoryId] = link.PullRequest.PullRequestNumber.HasValue
                ? link.PullRequest.ToPullRequestInfo()
                : null;
        }
        return result;
    }

    /// <summary>Inserts or updates the PR row for the given workspace–repo link. Pass null to persist "no PR" with LastCheckedAt.</summary>
    public async Task UpsertAsync(int workspaceRepositoryId, PullRequestInfo? pr, CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        var existing = await dbContext.WorkspaceRepositoryPullRequests
            .FirstOrDefaultAsync(prr => prr.WorkspaceRepositoryId == workspaceRepositoryId, cancellationToken);

        if (existing != null)
        {
            existing.PullRequestNumber = pr?.Number;
            existing.State = pr?.State;
            existing.Mergeable = pr?.Mergeable;
            existing.MergeableState = pr?.MergeableState;
            existing.HtmlUrl = pr?.HtmlUrl;
            existing.MergedAt = pr?.MergedAt;
            existing.LastCheckedAt = now;
        }
        else
        {
            dbContext.WorkspaceRepositoryPullRequests.Add(new WorkspaceRepositoryPullRequest
            {
                WorkspaceRepositoryId = workspaceRepositoryId,
                PullRequestNumber = pr?.Number,
                State = pr?.State,
                Mergeable = pr?.Mergeable,
                MergeableState = pr?.MergeableState,
                HtmlUrl = pr?.HtmlUrl,
                MergedAt = pr?.MergedAt,
                LastCheckedAt = now
            });
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        logger.LogTrace("Upserted PR for WorkspaceRepositoryId={WorkspaceRepositoryId}, PR#={Number}", workspaceRepositoryId, pr?.Number);
    }
}
