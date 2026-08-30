using System.Diagnostics;
using GrayMoon.Abstractions.Notifications;
using GrayMoon.App.Data;
using GrayMoon.App.Hubs;
using GrayMoon.App.Models;
using GrayMoon.App.Repositories;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace GrayMoon.App.Services.Agent;

/// <summary>Handles SyncCommand from the agent (hook flow): persist version/branch/commit counts and upstream flag, recompute dependency stats for the workspace, then broadcast WorkspaceSynced so the grid can refresh.</summary>
public sealed class SyncCommandHandler(
    IServiceScopeFactory scopeFactory,
    IHubContext<WorkspaceSyncHub> hubContext,
    ILogger<SyncCommandHandler> logger)
{
    public async Task HandleAsync(RepositorySyncNotification n)
    {
        var totalSw = Stopwatch.StartNew();
        await using var scope = scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var wr = await dbContext.WorkspaceRepositories
            .FirstOrDefaultAsync(wr => wr.WorkspaceId == n.WorkspaceId && wr.RepositoryId == n.RepositoryId);
        if (wr == null)
        {
            logger.LogWarning("SyncCommand: workspace {WorkspaceId} repo {RepositoryId} not found", n.WorkspaceId, n.RepositoryId);
            return;
        }

        var stateWriter = scope.ServiceProvider.GetRequiredService<WorkspaceRepositoryStateWriter>();
        var snapshot = n.State ?? BuildSnapshotFromFlatNotification(n);
        await stateWriter.ApplyAsync(n.WorkspaceId, n.RepositoryId, snapshot, new RepositoryStateWriteOptions
        {
            SyncStatus = SyncStatusWrite.Derive,
            ErrorMessageForcesInSync = true,
            ReconcilePullRequest = true,
        });

        var branchWriter = scope.ServiceProvider.GetRequiredService<RepositoryBranchWriter>();

        // Persist tags and compute HasNewerTag when the agent includes a tag list (checkout-to-tag sync).
        // Runs after the state write because it is what decides HasNewerTag, which the writer clears.
        if (n.RemoteTags != null)
        {
            await branchWriter.PersistAsync(
                wr.WorkspaceRepositoryId,
                localBranches: null,
                remoteBranches: null,
                defaultBranchName: null,
                tags: n.RemoteTags,
                currentTag: n.Tag,
                cancellationToken: default);
        }

        // Prune remote branches from DB that no longer exist in git (fetch --prune ran on the agent side).
        // The hook flow reports remotes without persisting a full branch list, so this cannot go through the
        // writer's branch-replace path.
        if (n.RemoteBranches != null && !snapshot.BranchesProbed)
        {
            var pruned = await branchWriter.PruneRemoteBranchesAsync(wr.WorkspaceRepositoryId, n.RemoteBranches);
            if (pruned > 0)
                logger.LogInformation("SyncCommand pruned {Count} stale remote branch(es) for repo {RepositoryId}", pruned, n.RepositoryId);
        }

        var allLinks = await dbContext.WorkspaceRepositories
            .Where(w => w.WorkspaceId == n.WorkspaceId)
            .Select(w => w.SyncStatus)
            .ToListAsync();
        var isInSync = allLinks.Count > 0 && allLinks.All(s => s == RepoSyncStatus.InSync);

        var workspace = await dbContext.Workspaces.FindAsync(n.WorkspaceId);
        if (workspace != null)
        {
            workspace.LastSyncedAt = DateTime.UtcNow;
            workspace.IsInSync = isInSync;
            await dbContext.SaveChangesAsync();
        }

        var depsSw = Stopwatch.StartNew();
        var recomputeScope = scope.ServiceProvider.GetRequiredService<WorkspaceStateRecomputeScope>();
        await recomputeScope.RecomputeAsync(n.WorkspaceId);
        logger.LogDebug(
            "SyncCommand dependency stats persisted in {ElapsedMs}ms for workspace={WorkspaceId}, repo={RepositoryId}",
            depsSw.ElapsedMilliseconds, n.WorkspaceId, n.RepositoryId);

        // RepositorySynced carries the repository id that WorkspaceActions targets its GitHub Actions
        // refresh with, so it stays per repository even though the workspace broadcast is coalesced.
        await hubContext.Clients.All.SendAsync("RepositorySynced", n.WorkspaceId, n.RepositoryId);
        await hubContext.Clients.All.SendAsync("WorkspaceSynced", n.WorkspaceId);
        if (!string.IsNullOrWhiteSpace(n.ErrorMessage))
            await hubContext.Clients.All.SendAsync("RepositoryError", n.WorkspaceId, n.RepositoryId, n.ErrorMessage);

        logger.LogDebug(
            "SyncCommand persisted in {ElapsedMs}ms: workspace={WorkspaceId}, repo={RepositoryId}, version={Version}, branch={Branch}",
            totalSw.ElapsedMilliseconds, n.WorkspaceId, n.RepositoryId, n.Version, n.Branch);
    }

    /// <summary>
    /// Builds a snapshot from the flat notification fields, for agents that predate
    /// <see cref="RepositorySyncNotification.State"/>. Only the groups those agents actually populate are
    /// marked probed, so an older agent can never clear a column it knows nothing about.
    /// </summary>
    private static RepositoryStateSnapshot BuildSnapshotFromFlatNotification(RepositorySyncNotification n)
    {
        var onTag = !string.IsNullOrWhiteSpace(n.Tag);
        return new RepositoryStateSnapshot
        {
            BranchName = n.Branch,
            CheckedOutTag = n.Tag,
            GitVersion = n.Version == "-" ? null : n.Version,
            OutgoingCommits = n.OutgoingCommits,
            IncomingCommits = n.IncomingCommits,
            DefaultBranchBehind = n.DefaultBranchBehind,
            DefaultBranchAhead = n.DefaultBranchAhead,
            HasUpstream = n.HasUpstream,
            Projects = n.Projects,
            ErrorMessage = n.ErrorMessage,
            IdentityProbed = true,
            GitVersionProbed = n.Version != "-",
            CommitCountsProbed = !onTag && (n.OutgoingCommits.HasValue || n.IncomingCommits.HasValue),
            UpstreamProbed = !onTag && n.HasUpstream.HasValue,
            // The flat shape has no local-branch or tag list, and its remote list is pruned separately.
            BranchesProbed = false,
            ProjectsProbed = n.Projects is { Count: > 0 },
        };
    }
}
