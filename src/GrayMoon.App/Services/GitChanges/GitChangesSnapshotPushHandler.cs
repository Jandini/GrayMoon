using GrayMoon.App.Data;
using GrayMoon.App.Hubs;
using GrayMoon.App.Models;
using GrayMoon.Common.Git;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace GrayMoon.App.Services.GitChanges;

/// <summary>
/// Persists an Agent-pushed Git Changes snapshot (rejecting versions older than or equal to what is
/// already persisted), then broadcasts <c>GitChangesUpdated</c>. Uses a fresh <see cref="AppDbContext"/>
/// via <see cref="IDbContextFactory{TContext}"/> rather than a shared scoped instance - snapshot pushes for
/// different repositories may be processed back-to-back on the same background worker and must never share
/// a DbContext across them. Mirrors <see cref="SyncCommandHandler"/>'s persist-then-broadcast shape.
/// </summary>
public sealed class GitChangesSnapshotPushHandler(
    IDbContextFactory<AppDbContext> dbContextFactory,
    IHubContext<WorkspaceSyncHub> hubContext,
    ILogger<GitChangesSnapshotPushHandler> logger)
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

        var snapshot = notification.Snapshot;
        var existing = await db.WorkspaceGitRepositoryStatuses
            .FirstOrDefaultAsync(s => s.WorkspaceRepositoryId == workspaceRepository.WorkspaceRepositoryId, cancellationToken);

        if (existing != null && snapshot.Version <= existing.SnapshotVersion)
        {
            logger.LogDebug(
                "GitChangesSnapshotUpdated: rejected stale snapshot version {IncomingVersion} <= persisted {PersistedVersion} for workspace {WorkspaceId} repo {RepositoryId}",
                snapshot.Version, existing.SnapshotVersion, notification.WorkspaceId, notification.RepositoryId);
            return;
        }

        var stagedCount = snapshot.Changes.Count(c => c.IsStaged);
        var changedCount = snapshot.Changes.Count(c => c.IsChanged);
        var conflictCount = snapshot.Changes.Count(c => c.IsConflicted);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        if (existing == null)
        {
            existing = new WorkspaceGitRepositoryStatus { WorkspaceRepositoryId = workspaceRepository.WorkspaceRepositoryId };
            db.WorkspaceGitRepositoryStatuses.Add(existing);
        }

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
        existing.AgentScannedAt = snapshot.ScannedAt;
        existing.PersistedAt = DateTimeOffset.UtcNow;
        existing.LastErrorCode = null;
        existing.LastErrorMessage = null;

        await db.SaveChangesAsync(cancellationToken);

        await db.WorkspaceGitChangeEntries
            .Where(e => e.WorkspaceRepositoryId == workspaceRepository.WorkspaceRepositoryId)
            .ExecuteDeleteAsync(cancellationToken);

        if (snapshot.Changes.Count > 0)
        {
            var rows = snapshot.Changes.Select(c => new WorkspaceGitChangeEntry
            {
                WorkspaceRepositoryId = workspaceRepository.WorkspaceRepositoryId,
                Path = c.Path,
                OriginalPath = c.OriginalPath,
                IndexChange = c.IndexChange,
                WorktreeChange = c.WorktreeChange,
                IsTracked = c.IsTracked,
                IsConflicted = c.IsConflicted,
                IsSubmodule = c.IsSubmodule,
            });
            db.WorkspaceGitChangeEntries.AddRange(rows);
            await db.SaveChangesAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);

        await hubContext.Clients.All.SendAsync("GitChangesUpdated", notification.WorkspaceId, notification.RepositoryId, cancellationToken: cancellationToken);

        logger.LogDebug(
            "GitChangesSnapshotUpdated persisted: workspace={WorkspaceId}, repo={RepositoryId}, version={Version}, staged={Staged}, changed={Changed}",
            notification.WorkspaceId, notification.RepositoryId, snapshot.Version, stagedCount, changedCount);
    }
}
