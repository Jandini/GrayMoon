using GrayMoon.App.Data;
using GrayMoon.Application.Features;
using Microsoft.EntityFrameworkCore;

namespace GrayMoon.App.Services.GitChanges;

/// <summary>
/// Read-only access to the persisted Git Changes projection. Never contacts the Agent - opening or
/// reloading the Git Changes page must read SQLite only, per the feature's architecture.
/// </summary>
public interface IWorkspaceGitChangesReadService
{
    Task<WorkspaceGitChangesView> GetWorkspaceAsync(int workspaceId, CancellationToken cancellationToken);

    Task<WorkspaceGitChangesView> GetContextAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        CancellationToken cancellationToken);
}

public sealed class WorkspaceGitChangesReadService(IDbContextFactory<AppDbContext> dbContextFactory) : IWorkspaceGitChangesReadService
{
    public Task<WorkspaceGitChangesView> GetWorkspaceAsync(int workspaceId, CancellationToken cancellationToken)
        => GetFromLegacyTablesAsync(workspaceId, cancellationToken);

    public async Task<WorkspaceGitChangesView> GetContextAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        CancellationToken cancellationToken)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        var statuses = await db.WorkspaceGitContextRepositoryStatuses
            .Where(s => s.WorkspaceFeatureContextId == contextId.Value
                        && db.WorkspaceRepositories.Any(wr =>
                            wr.WorkspaceRepositoryId == s.WorkspaceRepositoryId && wr.WorkspaceId == workspaceId))
            .ToListAsync(cancellationToken);

        if (statuses.Count == 0)
            return new WorkspaceGitChangesView { WorkspaceId = workspaceId, Repositories = [] };

        var workspaceRepositoryIds = statuses.Select(s => s.WorkspaceRepositoryId).ToList();

        var repoInfoById = await db.WorkspaceRepositories
            .Where(wr => workspaceRepositoryIds.Contains(wr.WorkspaceRepositoryId))
            .Select(wr => new { wr.WorkspaceRepositoryId, wr.RepositoryId, RepositoryName = wr.Repository!.RepositoryName, wr.DefaultBranchName })
            .ToDictionaryAsync(r => r.WorkspaceRepositoryId, cancellationToken);

        var entriesByRepo = (await db.WorkspaceGitContextChangeEntries
                .Where(e => e.WorkspaceFeatureContextId == contextId.Value
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
                    AgentScannedAt = s.AgentScannedAt,
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

    private async Task<WorkspaceGitChangesView> GetFromLegacyTablesAsync(int workspaceId, CancellationToken cancellationToken)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        var statuses = await db.WorkspaceGitRepositoryStatuses
            .Where(s => db.WorkspaceRepositories.Any(wr =>
                wr.WorkspaceRepositoryId == s.WorkspaceRepositoryId && wr.WorkspaceId == workspaceId))
            .ToListAsync(cancellationToken);

        if (statuses.Count == 0)
            return new WorkspaceGitChangesView { WorkspaceId = workspaceId, Repositories = [] };

        var workspaceRepositoryIds = statuses.Select(s => s.WorkspaceRepositoryId).ToList();

        var repoInfoById = await db.WorkspaceRepositories
            .Where(wr => workspaceRepositoryIds.Contains(wr.WorkspaceRepositoryId))
            .Select(wr => new { wr.WorkspaceRepositoryId, wr.RepositoryId, RepositoryName = wr.Repository!.RepositoryName, wr.DefaultBranchName })
            .ToDictionaryAsync(r => r.WorkspaceRepositoryId, cancellationToken);

        var entriesByRepo = (await db.WorkspaceGitChangeEntries
                .Where(e => workspaceRepositoryIds.Contains(e.WorkspaceRepositoryId))
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
                    AgentScannedAt = s.AgentScannedAt,
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
