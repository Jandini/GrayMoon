using System.Diagnostics;
using GrayMoon.App.Data;
using GrayMoon.App.Hubs;
using GrayMoon.App.Models;
using GrayMoon.Application.Features;
using GrayMoon.Common.Git;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GrayMoon.App.Services.GitChanges;

/// <summary>
/// Persists a Worker-pushed Git Changes snapshot (rejecting versions older than or equal to what is
/// already persisted), then broadcasts. Context tables are always written. Legacy special-Workspace
/// tables are written only when <see cref="GitChangesOptions.WriteLegacyGitChangesTables"/> is on.
/// </summary>
public sealed class GitChangesSnapshotPushHandler(
    IDbContextFactory<AppDbContext> dbContextFactory,
    IHubContext<WorkspaceSyncHub> hubContext,
    ILogger<GitChangesSnapshotPushHandler> logger,
    IGitChangesLineStatsRefresh lineStatsRefresh,
    IWorkspaceHookContextAttributor attributor,
    IWorkspaceFeatureContextResolver contextResolver,
    IWorkspaceGitChangesNotifier notifier,
    IOptions<GitChangesOptions> gitChangesOptions)
{
    public async Task HandleAsync(GitChangesSnapshotNotification notification, CancellationToken cancellationToken)
    {
        var options = gitChangesOptions.Value;
        var pathCount = notification.Snapshot.Changes.Count;

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

        var writeLegacy = options.WriteLegacyGitChangesTables && contextInfo.IsSpecialWorkspace;

        // Legacy table version gate only while dual-write is on for the special Workspace.
        WorkspaceGitRepositoryStatus? existingLegacy = null;
        if (writeLegacy)
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

        // Computed from the persisted values (line stats may carry over from the previous row), so equal digests
        // mean the page would paint exactly what it already shows.
        var contentDigest = GitChangesContentDigest.Compute(
            snapshot.Changes,
            existingContext.BranchName,
            existingContext.HeadCommit,
            existingContext.IsDetachedHead,
            existingContext.IsUnbornBranch,
            existingContext.IsMerging,
            existingContext.IsRebasing,
            existingContext.IsCherryPicking,
            existingContext.StagedCount,
            existingContext.ChangedCount,
            existingContext.ConflictCount,
            existingContext.Insertions,
            existingContext.Deletions,
            existingContext.StagedInsertions,
            existingContext.StagedDeletions);
        existingContext.ContentDigest = contentDigest;
        existingContext.PathCount = pathCount;

        if (writeLegacy)
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

        await db.WorkspaceGitContextChangeEntries
            .Where(e => e.WorkspaceFeatureContextId == contextId.Value.Value
                        && e.WorkspaceRepositoryId == workspaceRepository.WorkspaceRepositoryId)
            .ExecuteDeleteAsync(cancellationToken);

        if (writeLegacy)
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

            if (writeLegacy)
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
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        notifier.Publish(notification.WorkspaceId, contextId.Value.Value);

        await hubContext.Clients.All.SendAsync(
            "ContextGitChangesUpdated",
            notification.WorkspaceId,
            contextId.Value.Value,
            notification.RepositoryId,
            contentDigest,
            cancellationToken: cancellationToken);

        if (writeLegacy)
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
            lineStatsRefresh.RequestRepository(notification.WorkspaceId, notification.RepositoryId, contextId.Value);
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
        existing.WorkerScannedAt = snapshot.ScannedAt;
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
        existing.WorkerScannedAt = snapshot.ScannedAt;
        existing.PersistedAt = DateTimeOffset.UtcNow;
        existing.LastErrorCode = null;
        existing.LastErrorMessage = null;
    }
}
