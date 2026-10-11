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
    /// Context-aware counterpart of <see cref="GetByWorkspaceIdAsync"/> — reads
    /// <see cref="WorkspaceRepositoryContextPullRequest"/> for the given Feature context instead of the
    /// legacy workspace-link PR row. Keyed by RepositoryId. Missing row omitted (no PR or not yet checked).
    /// </summary>
    public async Task<IReadOnlyDictionary<int, PullRequestInfo?>> GetByWorkspaceIdContextAsync(
        int workspaceId, int contextId, CancellationToken cancellationToken = default)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        var rows = await dbContext.WorkspaceRepositoryContextPullRequests
            .AsNoTracking()
            .Where(pr => pr.WorkspaceFeatureContextId == contextId
                         && pr.WorkspaceRepository!.WorkspaceId == workspaceId)
            .Include(pr => pr.WorkspaceRepository)
            .ToListAsync(cancellationToken);

        var result = new Dictionary<int, PullRequestInfo?>();
        foreach (var row in rows)
        {
            if (row.WorkspaceRepository == null) continue;
            result[row.WorkspaceRepository.RepositoryId] = row.PullRequestNumber.HasValue
                ? row.ToPullRequestInfo()
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

    /// <summary>
    /// Inserts or updates every PR row on one factory context and one save. Null <see cref="PullRequestInfo"/>
    /// clears the scalar fields and keeps the row, matching <see cref="UpsertAsync"/>.
    /// </summary>
    public async Task UpsertManyAsync(
        IReadOnlyList<(int WorkspaceRepositoryId, PullRequestInfo? Pr)> items,
        CancellationToken cancellationToken = default)
    {
        if (items.Count == 0)
            return;

        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var ids = items.Select(i => i.WorkspaceRepositoryId).Distinct().ToList();
        var existing = await dbContext.WorkspaceRepositoryPullRequests
            .Where(prr => ids.Contains(prr.WorkspaceRepositoryId))
            .ToListAsync(cancellationToken);
        var byId = existing.ToDictionary(prr => prr.WorkspaceRepositoryId);
        var now = DateTime.UtcNow;
        foreach (var (workspaceRepositoryId, pr) in items)
        {
            if (!byId.TryGetValue(workspaceRepositoryId, out var row))
            {
                row = new WorkspaceRepositoryPullRequest { WorkspaceRepositoryId = workspaceRepositoryId };
                dbContext.WorkspaceRepositoryPullRequests.Add(row);
                byId[workspaceRepositoryId] = row;
            }

            ApplyLegacy(row, pr, now);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Inserts, updates, or removes the PR row for a Feature context (<see cref="WorkspaceRepositoryContextPullRequest"/>),
    /// keyed by (ContextId, WorkspaceRepositoryId) rather than the legacy WorkspaceRepositoryId-only row - see design
    /// doc §17. Pass null to remove the row (no PR / no branch to have one).
    /// </summary>
    public async Task UpsertContextAsync(int contextId, int workspaceRepositoryId, PullRequestInfo? pr, CancellationToken cancellationToken = default)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        var existing = await dbContext.WorkspaceRepositoryContextPullRequests
            .FirstOrDefaultAsync(
                prr => prr.WorkspaceFeatureContextId == contextId && prr.WorkspaceRepositoryId == workspaceRepositoryId,
                cancellationToken);

        if (pr == null)
        {
            if (existing != null)
            {
                dbContext.WorkspaceRepositoryContextPullRequests.Remove(existing);
                await dbContext.SaveChangesAsync(cancellationToken);
            }
            return;
        }

        var now = DateTime.UtcNow;
        if (existing != null)
        {
            existing.PullRequestNumber = pr.Number;
            existing.State = pr.State;
            existing.Mergeable = pr.Mergeable;
            existing.MergeableState = pr.MergeableState;
            existing.HtmlUrl = pr.HtmlUrl;
            existing.MergedAt = pr.MergedAt;
            existing.ChangedFiles = pr.ChangedFiles;
            existing.LastCheckedAt = now;
        }
        else
        {
            dbContext.WorkspaceRepositoryContextPullRequests.Add(new WorkspaceRepositoryContextPullRequest
            {
                WorkspaceFeatureContextId = contextId,
                WorkspaceRepositoryId = workspaceRepositoryId,
                PullRequestNumber = pr.Number,
                State = pr.State,
                Mergeable = pr.Mergeable,
                MergeableState = pr.MergeableState,
                HtmlUrl = pr.HtmlUrl,
                MergedAt = pr.MergedAt,
                ChangedFiles = pr.ChangedFiles,
                LastCheckedAt = now
            });
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        logger.LogTrace(
            "Upserted context PR for ContextId={ContextId}, WorkspaceRepositoryId={WorkspaceRepositoryId}, PR#={Number}",
            contextId, workspaceRepositoryId, pr.Number);
    }

    /// <summary>
    /// Inserts, updates, or removes every context PR row on one factory context and one save.
    /// Null <see cref="PullRequestInfo"/> deletes the row, matching <see cref="UpsertContextAsync"/>.
    /// </summary>
    public async Task UpsertContextManyAsync(
        int contextId,
        IReadOnlyList<(int WorkspaceRepositoryId, PullRequestInfo? Pr)> items,
        CancellationToken cancellationToken = default)
    {
        if (items.Count == 0)
            return;

        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var ids = items.Select(i => i.WorkspaceRepositoryId).Distinct().ToList();
        var existing = await dbContext.WorkspaceRepositoryContextPullRequests
            .Where(prr => prr.WorkspaceFeatureContextId == contextId && ids.Contains(prr.WorkspaceRepositoryId))
            .ToListAsync(cancellationToken);
        var byId = existing.ToDictionary(prr => prr.WorkspaceRepositoryId);
        var now = DateTime.UtcNow;
        foreach (var (workspaceRepositoryId, pr) in items)
        {
            byId.TryGetValue(workspaceRepositoryId, out var row);
            if (pr == null)
            {
                if (row != null)
                {
                    dbContext.WorkspaceRepositoryContextPullRequests.Remove(row);
                    byId.Remove(workspaceRepositoryId);
                }

                continue;
            }

            if (row == null)
            {
                row = new WorkspaceRepositoryContextPullRequest
                {
                    WorkspaceFeatureContextId = contextId,
                    WorkspaceRepositoryId = workspaceRepositoryId
                };
                dbContext.WorkspaceRepositoryContextPullRequests.Add(row);
                byId[workspaceRepositoryId] = row;
            }

            ApplyContext(row, pr, now);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static void ApplyLegacy(WorkspaceRepositoryPullRequest row, PullRequestInfo? pr, DateTime now)
    {
        row.PullRequestNumber = pr?.Number;
        row.State = pr?.State;
        row.Mergeable = pr?.Mergeable;
        row.MergeableState = pr?.MergeableState;
        row.HtmlUrl = pr?.HtmlUrl;
        row.MergedAt = pr?.MergedAt;
        row.ChangedFiles = pr?.ChangedFiles;
        row.LastCheckedAt = now;
    }

    private static void ApplyContext(WorkspaceRepositoryContextPullRequest row, PullRequestInfo pr, DateTime now)
    {
        row.PullRequestNumber = pr.Number;
        row.State = pr.State;
        row.Mergeable = pr.Mergeable;
        row.MergeableState = pr.MergeableState;
        row.HtmlUrl = pr.HtmlUrl;
        row.MergedAt = pr.MergedAt;
        row.ChangedFiles = pr.ChangedFiles;
        row.LastCheckedAt = now;
    }
}
