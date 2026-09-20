using System.Diagnostics;
using GrayMoon.Abstractions.Notifications;
using GrayMoon.App.Data;
using GrayMoon.App.Hubs;
using GrayMoon.App.Models;
using GrayMoon.App.Repositories;
using GrayMoon.Application.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace GrayMoon.App.Services.Agent;

/// <summary>Handles SyncCommand from the agent (hook flow): attribute path to a Feature context, persist state, recompute deps, broadcast.</summary>
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
        var attributor = scope.ServiceProvider.GetRequiredService<IWorkspaceHookContextAttributor>();

        var wr = await dbContext.WorkspaceRepositories
            .FirstOrDefaultAsync(wr => wr.WorkspaceId == n.WorkspaceId && wr.RepositoryId == n.RepositoryId);
        if (wr == null)
        {
            logger.LogWarning("SyncCommand: workspace {WorkspaceId} repo {RepositoryId} not found", n.WorkspaceId, n.RepositoryId);
            return;
        }

        var contextId = await attributor.ResolveAsync(
            n.WorkspaceId,
            n.RepositoryId,
            n.RepositoryPath,
            n.WorkspaceFeatureContextId);
        if (contextId is null)
        {
            logger.LogWarning(
                "SyncCommand: skipping state write for workspace {WorkspaceId} repo {RepositoryId} path {RepositoryPath}",
                n.WorkspaceId, n.RepositoryId, n.RepositoryPath);
            return;
        }

        var contextInfo = await scope.ServiceProvider
            .GetRequiredService<IWorkspaceFeatureContextResolver>()
            .GetRequiredAsync(contextId.Value, n.WorkspaceId);

        var stateWriter = scope.ServiceProvider.GetRequiredService<WorkspaceRepositoryStateWriter>();
        var snapshot = n.State ?? BuildSnapshotFromFlatNotification(n);
        await stateWriter.ApplyAsync(contextId.Value, n.WorkspaceId, n.RepositoryId, snapshot, new RepositoryStateWriteOptions
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

        await UpdateContextSyncFlagsAsync(dbContext, contextId.Value, n.WorkspaceId, contextInfo.IsSpecialWorkspace);

        var depsSw = Stopwatch.StartNew();
        var recomputeScope = scope.ServiceProvider.GetRequiredService<WorkspaceStateRecomputeScope>();
        await recomputeScope.RecomputeAsync(n.WorkspaceId, contextId.Value);
        logger.LogDebug(
            "SyncCommand dependency stats persisted in {ElapsedMs}ms for workspace={WorkspaceId}, repo={RepositoryId}",
            depsSw.ElapsedMilliseconds, n.WorkspaceId, n.RepositoryId);

        // Context-aware broadcasts. Legacy WorkspaceSynced/RepositorySynced remain for special Workspace
        // so existing page listeners keep working during the Features cutover.
        await hubContext.Clients.All.SendAsync(
            "ContextRepositorySynced", n.WorkspaceId, contextId.Value.Value, n.RepositoryId);
        await hubContext.Clients.All.SendAsync(
            "ContextSynced", n.WorkspaceId, contextId.Value.Value);

        if (contextInfo.IsSpecialWorkspace)
        {
            await hubContext.Clients.All.SendAsync("RepositorySynced", n.WorkspaceId, n.RepositoryId);
            await hubContext.Clients.All.SendAsync("WorkspaceSynced", n.WorkspaceId);
        }

        if (!string.IsNullOrWhiteSpace(n.ErrorMessage))
            await hubContext.Clients.All.SendAsync("RepositoryError", n.WorkspaceId, n.RepositoryId, n.ErrorMessage);

        logger.LogDebug(
            "SyncCommand persisted in {ElapsedMs}ms: workspace={WorkspaceId}, context={ContextId}, repo={RepositoryId}, version={Version}, branch={Branch}",
            totalSw.ElapsedMilliseconds, n.WorkspaceId, contextId.Value.Value, n.RepositoryId, n.Version, n.Branch);
    }

    private static async Task UpdateContextSyncFlagsAsync(
        AppDbContext dbContext,
        WorkspaceFeatureContextId contextId,
        int workspaceId,
        bool isSpecialWorkspace)
    {
        var context = await dbContext.WorkspaceFeatureContexts
            .FirstOrDefaultAsync(c => c.WorkspaceFeatureContextId == contextId.Value);
        if (context is null)
            return;

        var states = await dbContext.WorkspaceRepositoryContextStates
            .Where(s => s.WorkspaceFeatureContextId == contextId.Value)
            .Select(s => s.SyncStatus)
            .ToListAsync();

        // Context states may still be empty during dual-write; fall back to link status for Workspace.
        var isInSync = states.Count > 0
            ? states.All(s => s == RepoSyncStatus.InSync)
            : false;

        if (states.Count == 0 && isSpecialWorkspace)
        {
            var linkStatuses = await dbContext.WorkspaceRepositories
                .Where(w => w.WorkspaceId == workspaceId)
                .Select(w => w.SyncStatus)
                .ToListAsync();
            isInSync = linkStatuses.Count > 0 && linkStatuses.All(s => s == RepoSyncStatus.InSync);
        }

        context.LastSyncedAt = DateTime.UtcNow;
        context.IsInSync = isInSync;

        if (isSpecialWorkspace)
        {
            var workspace = await dbContext.Workspaces.FindAsync(workspaceId);
            if (workspace != null)
            {
                workspace.LastSyncedAt = context.LastSyncedAt;
                workspace.IsInSync = isInSync;
            }
        }

        await dbContext.SaveChangesAsync();
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
