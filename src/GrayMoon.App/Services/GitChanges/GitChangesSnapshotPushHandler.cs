using GrayMoon.App.Data;
using GrayMoon.App.Hubs;
using GrayMoon.App.Models;
using GrayMoon.Application.Features;
using GrayMoon.Common.Git;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace GrayMoon.App.Services.GitChanges;

/// <summary>
/// Persists an Agent-pushed Git Changes snapshot (rejecting versions older than or equal to what is
/// already persisted), then broadcasts. Dual-writes context projection tables; legacy tables remain
/// authoritative for the special Workspace during cutover.
/// </summary>
public sealed class GitChangesSnapshotPushHandler(
    IDbContextFactory<AppDbContext> dbContextFactory,
    IHubContext<WorkspaceSyncHub> hubContext,
    ILogger<GitChangesSnapshotPushHandler> logger,
    IGitChangesLineStatsRefresh lineStatsRefresh,
    IWorkspaceHookContextAttributor attributor,
    IWorkspaceFeatureContextResolver contextResolver)
{
    public async Task HandleAsync(GitChangesSnapshotNotification notification, CancellationToken cancellationToken)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        var workspaceRepository = await db.WorkspaceRepositories
            .FirstOrDefaultAsync(
                wr => wr.WorkspaceId == notification.WorkspaceId && wr.RepositoryId == notification.RepositoryId,
                cancellationToken);

        if (workspaceRepository == null)
        {
            logger.LogWarning(
                "GitChangesSnapshotUpdated: workspace {WorkspaceId} repo {RepositoryId} not found",
                notification.WorkspaceId,
                notification.RepositoryId);
            return;
        }

        var contextId = await attributor.ResolveAsync(
            notification.WorkspaceId,
            notification.RepositoryId,
            notification.RepositoryPath,
            cancellationToken: cancellationToken);
        if (contextId is null)
        {
            logger.LogWarning(
                "GitChangesSnapshotUpdated: skipping write for unknown path {RepositoryPath} workspace {WorkspaceId} repo {RepositoryId}",
                notification.RepositoryPath, notification.WorkspaceId, notification.RepositoryId);
            return;
        }

        var contextInfo = await contextResolver.GetRequiredAsync(contextId.Value, notification.WorkspaceId, cancellationToken);
        var snapshot = notification.Snapshot;

        var existingContext = await db.WorkspaceGitContextRepositoryStatuses
            .FirstOrDefaultAsync(
                s => s.WorkspaceFeatureContextId == contextId.Value.Value
                     && s.WorkspaceRepositoryId == workspaceRepository.WorkspaceRepositoryId,
                cancellationToken);

        if (existingContext != null && snapshot.Version <= existingContext.SnapshotVersion)
        {
            logger.LogDebug(
                "GitChangesSnapshotUpdated: rejected stale context snapshot version {IncomingVersion} <= persisted {PersistedVersion}",
                snapshot.Version, existingContext.SnapshotVersion);
            return;
        }

        // Legacy table version gate only for special Workspace dual-write.
        WorkspaceGitRepositoryStatus? existingLegacy = null;
        if (contextInfo.IsSpecialWorkspace)
        {
            existingLegacy = await db.WorkspaceGitRepositoryStatuses
                .FirstOrDefaultAsync(s => s.WorkspaceRepositoryId == workspaceRepository.WorkspaceRepositoryId, cancellationToken);
            if (existingLegacy != null && snapshot.Version <= existingLegacy.SnapshotVersion)
            {
                logger.LogDebug(
                    "GitChangesSnapshotUpdated: rejected stale legacy snapshot version {IncomingVersion} <= persisted {PersistedVersion}",
                    snapshot.Version, existingLegacy.SnapshotVersion);
                return;
            }
        }

        var stagedCount = snapshot.Changes.Count(c => c.IsStaged);
        var changedCount = snapshot.Changes.Count(c => c.IsChanged);
        var conflictCount = snapshot.Changes.Count(c => c.IsConflicted);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        if (existingContext == null)
        {
            existingContext = new WorkspaceGitContextRepositoryStatus
            {
                WorkspaceFeatureContextId = contextId.Value.Value,
                WorkspaceRepositoryId = workspaceRepository.WorkspaceRepositoryId
            };
            db.WorkspaceGitContextRepositoryStatuses.Add(existingContext);
        }

        ApplyStatus(existingContext, snapshot, stagedCount, changedCount, conflictCount);

        if (contextInfo.IsSpecialWorkspace)
        {
            if (existingLegacy == null)
            {
                existingLegacy = new WorkspaceGitRepositoryStatus
                {
                    WorkspaceRepositoryId = workspaceRepository.WorkspaceRepositoryId
                };
                db.WorkspaceGitRepositoryStatuses.Add(existingLegacy);
            }

            ApplyLegacyStatus(existingLegacy, snapshot, stagedCount, changedCount, conflictCount);
        }

        await db.SaveChangesAsync(cancellationToken);

        await db.WorkspaceGitContextChangeEntries
            .Where(e => e.WorkspaceFeatureContextId == contextId.Value.Value
                        && e.WorkspaceRepositoryId == workspaceRepository.WorkspaceRepositoryId)
            .ExecuteDeleteAsync(cancellationToken);

        if (contextInfo.IsSpecialWorkspace)
        {
            await db.WorkspaceGitChangeEntries
                .Where(e => e.WorkspaceRepositoryId == workspaceRepository.WorkspaceRepositoryId)
                .ExecuteDeleteAsync(cancellationToken);
        }

        if (snapshot.Changes.Count > 0)
        {
            db.WorkspaceGitContextChangeEntries.AddRange(snapshot.Changes.Select(c => new WorkspaceGitContextChangeEntry
            {
                WorkspaceFeatureContextId = contextId.Value.Value,
                WorkspaceRepositoryId = workspaceRepository.WorkspaceRepositoryId,
                Path = c.Path,
                OriginalPath = c.OriginalPath,
                IndexChange = c.IndexChange,
                WorktreeChange = c.WorktreeChange,
                IsTracked = c.IsTracked,
                IsConflicted = c.IsConflicted,
                IsSubmodule = c.IsSubmodule,
            }));

            if (contextInfo.IsSpecialWorkspace)
            {
                db.WorkspaceGitChangeEntries.AddRange(snapshot.Changes.Select(c => new WorkspaceGitChangeEntry
                {
                    WorkspaceRepositoryId = workspaceRepository.WorkspaceRepositoryId,
                    Path = c.Path,
                    OriginalPath = c.OriginalPath,
                    IndexChange = c.IndexChange,
                    WorktreeChange = c.WorktreeChange,
                    IsTracked = c.IsTracked,
                    IsConflicted = c.IsConflicted,
                    IsSubmodule = c.IsSubmodule,
                }));
            }

            await db.SaveChangesAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);

        await hubContext.Clients.All.SendAsync(
            "ContextGitChangesUpdated",
            notification.WorkspaceId,
            contextId.Value.Value,
            notification.RepositoryId,
            cancellationToken: cancellationToken);

        if (contextInfo.IsSpecialWorkspace)
        {
            await hubContext.Clients.All.SendAsync(
                "GitChangesUpdated",
                notification.WorkspaceId,
                notification.RepositoryId,
                cancellationToken: cancellationToken);
        }

        if (!snapshot.Insertions.HasValue
            && !snapshot.Deletions.HasValue
            && (changedCount > 0 || stagedCount > 0))
        {
            lineStatsRefresh.RequestRepository(notification.WorkspaceId, notification.RepositoryId);
        }

        logger.LogDebug(
            "GitChangesSnapshotUpdated persisted: workspace={WorkspaceId}, context={ContextId}, repo={RepositoryId}, version={Version}",
            notification.WorkspaceId, contextId.Value.Value, notification.RepositoryId, snapshot.Version);
    }

    private static void ApplyStatus(
        WorkspaceGitContextRepositoryStatus existing,
        GitChangeSnapshot snapshot,
        int stagedCount,
        int changedCount,
        int conflictCount)
    {
        existing.SnapshotVersion = snapshot.Version;
        existing.BranchName = snapshot.BranchName;
        existing.HeadCommit = snapshot.HeadCommit;
        existing.IsDetachedHead = snapshot.IsDetachedHead;
        existing.IsUnbornBranch = snapshot.IsUnbornBranch;
        existing.IsMerging = snapshot.IsMerging;
        existing.IsRebasing = snapshot.IsRebasing;
        existing.IsCherryPicking = snapshot.IsCherryPicking;
        existing.StagedCount = stagedCount;
        existing.ChangedCount = changedCount;
        existing.ConflictCount = conflictCount;
        existing.Insertions = snapshot.Insertions.HasValue
            ? snapshot.Insertions
            : changedCount == 0 ? 0 : existing.Insertions;
        existing.Deletions = snapshot.Deletions.HasValue
            ? snapshot.Deletions
            : changedCount == 0 ? 0 : existing.Deletions;
        existing.StagedInsertions = snapshot.StagedInsertions.HasValue
            ? snapshot.StagedInsertions
            : stagedCount == 0 ? 0 : existing.StagedInsertions;
        existing.StagedDeletions = snapshot.StagedDeletions.HasValue
            ? snapshot.StagedDeletions
            : stagedCount == 0 ? 0 : existing.StagedDeletions;
        existing.AgentScannedAt = snapshot.ScannedAt;
        existing.PersistedAt = DateTimeOffset.UtcNow;
        existing.LastErrorCode = null;
        existing.LastErrorMessage = null;
    }

    private static void ApplyLegacyStatus(
        WorkspaceGitRepositoryStatus existing,
        GitChangeSnapshot snapshot,
        int stagedCount,
        int changedCount,
        int conflictCount)
    {
        existing.SnapshotVersion = snapshot.Version;
        existing.BranchName = snapshot.BranchName;
        existing.HeadCommit = snapshot.HeadCommit;
        existing.IsDetachedHead = snapshot.IsDetachedHead;
        existing.IsUnbornBranch = snapshot.IsUnbornBranch;
        existing.IsMerging = snapshot.IsMerging;
        existing.IsRebasing = snapshot.IsRebasing;
        existing.IsCherryPicking = snapshot.IsCherryPicking;
        existing.StagedCount = stagedCount;
        existing.ChangedCount = changedCount;
        existing.ConflictCount = conflictCount;
        existing.Insertions = snapshot.Insertions.HasValue
            ? snapshot.Insertions
            : changedCount == 0 ? 0 : existing.Insertions;
        existing.Deletions = snapshot.Deletions.HasValue
            ? snapshot.Deletions
            : changedCount == 0 ? 0 : existing.Deletions;
        existing.StagedInsertions = snapshot.StagedInsertions.HasValue
            ? snapshot.StagedInsertions
            : stagedCount == 0 ? 0 : existing.StagedInsertions;
        existing.StagedDeletions = snapshot.StagedDeletions.HasValue
            ? snapshot.StagedDeletions
            : stagedCount == 0 ? 0 : existing.StagedDeletions;
        existing.AgentScannedAt = snapshot.ScannedAt;
        existing.PersistedAt = DateTimeOffset.UtcNow;
        existing.LastErrorCode = null;
        existing.LastErrorMessage = null;
    }
}
