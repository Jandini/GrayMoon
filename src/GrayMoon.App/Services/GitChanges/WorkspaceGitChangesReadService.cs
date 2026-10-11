using GrayMoon.App.Data;
using GrayMoon.App.Models;
using GrayMoon.Application.Features;
using Microsoft.EntityFrameworkCore;

namespace GrayMoon.App.Services.GitChanges;

/// <summary>
/// Read-only access to the persisted Git Changes projection. Never contacts the Worker - opening or
/// reloading the Git Changes page must read SQLite only, per the feature's architecture.
/// </summary>
public interface IWorkspaceGitChangesReadService
{
    Task<WorkspaceGitChangesView> GetWorkspaceAsync(int workspaceId, CancellationToken cancellationToken);

    Task<WorkspaceGitChangesView> GetContextAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        CancellationToken cancellationToken);

    /// <summary>True when any repository in the context has a staged, unstaged, or conflicted file.</summary>
    Task<bool> HasAnyChangesAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        CancellationToken cancellationToken);
}

public sealed class WorkspaceGitChangesReadService(IDbContextFactory<AppDbContext> dbContextFactory) : IWorkspaceGitChangesReadService
{
    public async Task<WorkspaceGitChangesView> GetWorkspaceAsync(int workspaceId, CancellationToken cancellationToken)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var contextId = await db.WorkspaceFeatureContexts
            .AsNoTracking()
            .Where(c => c.WorkspaceId == workspaceId && c.Kind == WorkspaceFeatureContextKind.Workspace)
            .Select(c => (int?)c.WorkspaceFeatureContextId)
            .FirstOrDefaultAsync(cancellationToken);
        if (contextId is null)
            return new WorkspaceGitChangesView { WorkspaceId = workspaceId, Repositories = [] };

        return await LoadContextAsync(db, workspaceId, contextId.Value, cancellationToken);
    }

    public async Task<WorkspaceGitChangesView> GetContextAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        CancellationToken cancellationToken)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await LoadContextAsync(db, workspaceId, contextId.Value, cancellationToken);
    }

    public async Task<bool> HasAnyChangesAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        CancellationToken cancellationToken)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        return await (
            from s in db.WorkspaceGitContextRepositoryStatuses.AsNoTracking()
            join wr in db.WorkspaceRepositories.AsNoTracking()
                on s.WorkspaceRepositoryId equals wr.WorkspaceRepositoryId
            where s.WorkspaceFeatureContextId == contextId.Value
                  && wr.WorkspaceId == workspaceId
                  && (s.StagedCount > 0 || s.ChangedCount > 0 || s.ConflictCount > 0)
            select s).AnyAsync(cancellationToken);
    }

    private static async Task<WorkspaceGitChangesView> LoadContextAsync(
        AppDbContext db,
        int workspaceId,
        int contextId,
        CancellationToken cancellationToken)
    {
        var statuses = await (
            from s in db.WorkspaceGitContextRepositoryStatuses.AsNoTracking()
            join wr in db.WorkspaceRepositories.AsNoTracking()
                on s.WorkspaceRepositoryId equals wr.WorkspaceRepositoryId
            where s.WorkspaceFeatureContextId == contextId && wr.WorkspaceId == workspaceId
            select s).ToListAsync(cancellationToken);

        if (statuses.Count == 0)
            return new WorkspaceGitChangesView { WorkspaceId = workspaceId, Repositories = [] };

        var workspaceRepositoryIds = statuses.Select(s => s.WorkspaceRepositoryId).ToList();

        var repoInfoById = await db.WorkspaceRepositories
            .AsNoTracking()
            .Where(wr => workspaceRepositoryIds.Contains(wr.WorkspaceRepositoryId))
            .Select(wr => new { wr.WorkspaceRepositoryId, wr.RepositoryId, RepositoryName = wr.Repository!.RepositoryName, wr.DefaultBranchName })
            .ToDictionaryAsync(r => r.WorkspaceRepositoryId, cancellationToken);

        var entriesByRepo = (await db.WorkspaceGitContextChangeEntries
                .AsNoTracking()
                .Where(e => e.WorkspaceFeatureContextId == contextId
                            && workspaceRepositoryIds.Contains(e.WorkspaceRepositoryId))
                .ToListAsync(cancellationToken))
            .GroupBy(e => e.WorkspaceRepositoryId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var repositories = statuses
            .Where(s => repoInfoById.ContainsKey(s.WorkspaceRepositoryId))
            .Select(s =>
            {
                var info = repoInfoById[s.WorkspaceRepositoryId];
                var changes = entriesByRepo.TryGetValue(s.WorkspaceRepositoryId, out var list)
                    ? list.Select(e => new WorkspaceGitChangeEntryView
                    {
                        Path = e.Path,
                        OriginalPath = e.OriginalPath,
                        IndexChange = e.IndexChange,
                        WorktreeChange = e.WorktreeChange,
                        IsTracked = e.IsTracked,
                        IsConflicted = e.IsConflicted,
                        IsSubmodule = e.IsSubmodule,
                    }).ToList()
                    : (IReadOnlyList<WorkspaceGitChangeEntryView>)[];

                return new WorkspaceGitChangesRepositoryView
                {
                    WorkspaceRepositoryId = s.WorkspaceRepositoryId,
                    RepositoryId = info.RepositoryId,
                    RepositoryName = info.RepositoryName,
                    BranchName = s.BranchName,
                    DefaultBranchName = info.DefaultBranchName,
                    HeadCommit = s.HeadCommit,
                    IsDetachedHead = s.IsDetachedHead,
                    IsUnbornBranch = s.IsUnbornBranch,
                    IsMerging = s.IsMerging,
                    IsRebasing = s.IsRebasing,
                    IsCherryPicking = s.IsCherryPicking,
                    StagedCount = s.StagedCount,
                    ChangedCount = s.ChangedCount,
                    ConflictCount = s.ConflictCount,
                    Insertions = s.Insertions,
                    Deletions = s.Deletions,
                    StagedInsertions = s.StagedInsertions,
                    StagedDeletions = s.StagedDeletions,
                    WorkerScannedAt = s.WorkerScannedAt,
                    PersistedAt = s.PersistedAt,
                    LastErrorCode = s.LastErrorCode,
                    LastErrorMessage = s.LastErrorMessage,
                    Changes = changes,
                };
            })
            .OrderBy(r => r.RepositoryName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new WorkspaceGitChangesView { WorkspaceId = workspaceId, Repositories = repositories };
    }
}
