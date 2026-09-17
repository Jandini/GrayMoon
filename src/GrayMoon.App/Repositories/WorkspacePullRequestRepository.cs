using GrayMoon.App.Data;
using GrayMoon.App.Models;
using Microsoft.EntityFrameworkCore;

namespace GrayMoon.App.Repositories;

/// <summary>Persistence for pull request state per workspace-repository link. Single place for all PR table read/write.</summary>
public sealed class WorkspacePullRequestRepository(IDbContextFactory<AppDbContext> dbContextFactory, ILogger<WorkspacePullRequestRepository> logger)
{
    /// <summary>Returns persisted PR state for all repositories in the workspace, keyed by RepositoryId. Missing row yields null (no PR or not yet checked).</summary>
    public async Task<IReadOnlyDictionary<int, PullRequestInfo?>> GetByWorkspaceIdAsync(int workspaceId, CancellationToken cancellationToken = default)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);

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

    /// <summary>
    /// Inserts or updates the PR row for the given workspace-repo link. Pass null to persist "no PR" with LastCheckedAt.
    /// Uses its own factory-created <see cref="AppDbContext"/> (rather than a shared, circuit-scoped instance) because
    /// callers such as bulk merge run many of these concurrently via a bounded <c>Task.WhenAll</c> fan-out - a shared
    /// context there throws "A second operation was started on this context instance before a previous operation
    /// completed." and can leave the shared instance unusable for the rest of the circuit (e.g. the Git Changes page).
    /// </summary>
    public async Task UpsertAsync(int workspaceRepositoryId, PullRequestInfo? pr, CancellationToken cancellationToken = default)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);

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
            existing.ChangedFiles = pr?.ChangedFiles;
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
                ChangedFiles = pr?.ChangedFiles,
                LastCheckedAt = now
            });
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        logger.LogTrace("Upserted PR for WorkspaceRepositoryId={WorkspaceRepositoryId}, PR#={Number}", workspaceRepositoryId, pr?.Number);
    }
}
