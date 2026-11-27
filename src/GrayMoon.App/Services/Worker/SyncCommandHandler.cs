using System.Diagnostics;
using GrayMoon.Abstractions.Notifications;
using GrayMoon.App.Data;
using GrayMoon.App.Hubs;
using GrayMoon.App.Models;
using GrayMoon.App.Repositories;
using GrayMoon.App.Services.Features;
using GrayMoon.App.Services.Workspaces;
using GrayMoon.App.Services.WorkspaceManifest;
using GrayMoon.Application.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace GrayMoon.App.Services.Worker;

/// <summary>
/// Handles SyncCommand from the worker (hook flow) in two steps. <see cref="ApplyAsync"/> is the per-repository part:
/// attribute the path to a context and persist that repository's state. <see cref="CompleteAsync"/> is the per-context
/// part: one recompute and the broadcasts for every notification applied since the last completion.
/// <see cref="WorkerSyncNotificationQueue"/> applies a burst item by item and completes it once.
/// </summary>
public sealed class SyncCommandHandler(
    IServiceScopeFactory scopeFactory,
    IHubContext<WorkspaceSyncHub> hubContext,
    ILogger<SyncCommandHandler> logger) : ISyncNotificationHandler
{
    /// <summary>Applies one notification and completes it at once. The sync queue batches instead.</summary>
    /// <param name="n">The notification the Worker sent.</param>
    /// <param name="receivedAt">
    /// <see cref="RepositoryStateFreshness.Now"/> read when the notification reached the App, before it waited in the
    /// sync queue. Null means it is applied as it stands.
    /// </param>
    public async Task HandleAsync(RepositorySyncNotification n, long? receivedAt = null)
    {
        var observer = new SingleSyncObserver();
        try
        {
            await ApplyAsync(n, receivedAt, observer);
        }
        finally
        {
            // A state write that succeeded is recomputed and announced even when a later step of the apply threw.
            if (observer.Applied is { } applied)
                await CompleteAsync([applied]);
        }
    }

    private sealed class SingleSyncObserver : ISyncApplyObserver
    {
        public AppliedSync? Applied { get; private set; }

        /// <summary>One notification applied on its own waits for its Feature here; nothing else is behind it.</summary>
        public async Task<bool> ContextResolvedAsync(int workspaceId, WorkspaceFeatureContextId contextId, Task<bool>? finalization)
        {
            if (finalization is not null)
                await finalization;
            return true;
        }

        public void StateWritten(AppliedSync applied) => Applied = applied;
    }

    /// <inheritdoc />
    public async Task ApplyAsync(RepositorySyncNotification n, long? receivedAt, ISyncApplyObserver observer)
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

        // A Feature that is still being created or repaired owns its projections until the seed has finished: the
        // Worker releases its deferred checkout syncs as soon as the worktrees exist, which is before the seed. Applying
        // such a sync afterwards keeps the Feature's own GitVersion (the seed never writes it) and runs the recompute on
        // the finished graph. Who waits is the observer's decision: the sync queue sets the notification aside, so the
        // notifications of every other context are applied meanwhile.
        Task<bool>? finalization = null;
        var coordinator = scope.ServiceProvider.GetService<FeatureFinalizationCoordinator>();
        if (coordinator is not null && !contextInfo.IsSpecialWorkspace && coordinator.IsFinalizing(contextId.Value.Value))
        {
            logger.LogInformation(
                "SyncCommand: holding sync for workspace {WorkspaceId} context {ContextId} repo {RepositoryId} until Feature finalization completes",
                n.WorkspaceId, contextId.Value.Value, n.RepositoryId);
            finalization = WaitForFinalizationAsync(coordinator, contextId.Value.Value);
        }

        // Before the state write: the queue completes the batch of another context here, or takes this notification
        // back when its Feature is not ready for it.
        if (!await observer.ContextResolvedAsync(n.WorkspaceId, contextId.Value, finalization))
            return;

        var stateWriter = scope.ServiceProvider.GetRequiredService<WorkspaceRepositoryStateWriter>();
        // App and Worker are version-locked, and every Worker notification carries the snapshot with its probe
        // markers. One without it says nothing GrayMoon can store: guessing the markers from the flat fields once
        // marked a checkout as read whose commit was never reported.
        if (n.State is not { } snapshot)
        {
            logger.LogWarning(
                "SyncCommand: notification without a state snapshot ignored: workspace {WorkspaceId} repo {RepositoryId}",
                n.WorkspaceId, n.RepositoryId);
            return;
        }

        // The queue is serial and a burst of hooks can hold a snapshot for minutes. A pull or sync that began after
        // the snapshot arrived has read the checkout since, so the groups it wrote are newer than the snapshot's. And
        // of two hook snapshots the one the Worker captured first is the older one, whichever arrived first.
        if (receivedAt is { } received
            && scope.ServiceProvider.GetService<RepositoryStateFreshness>() is { } freshness)
        {
            var verdict = freshness.Evaluate(contextId.Value, n.RepositoryId, snapshot, received, n.WorkspaceId);
            if (verdict.Snapshot is null)
            {
                logger.LogInformation(
                    "SyncCommand: dropped a snapshot ({Why}): workspace={WorkspaceId}, context={ContextId}, repo={RepositoryId}, version={Version}, branch={Branch}",
                    verdict.Duplicate ? "already applied" : "superseded by newer evidence",
                    n.WorkspaceId, contextId.Value.Value, n.RepositoryId, n.Version, n.Branch);
                return;
            }

            if (!ReferenceEquals(verdict.Snapshot, snapshot))
            {
                logger.LogInformation(
                    "SyncCommand: applying only the groups newer evidence did not supersede ({Superseded} left out): workspace={WorkspaceId}, context={ContextId}, repo={RepositoryId}",
                    verdict.Superseded, n.WorkspaceId, contextId.Value.Value, n.RepositoryId);
                snapshot = verdict.Snapshot;
            }

            // Two captures overlapped on the Worker: neither is known to be the later one. The snapshot is applied
            // (it may be the newer), and the repository is read again so that what is stored is not a guess.
            if (verdict.Ambiguous != RepositoryFactGroups.None)
            {
                scope.ServiceProvider.GetService<IRepositoryStateReconciler>()?.Request(
                    n.WorkspaceId, contextId.Value, n.RepositoryId, $"overlapping captures ({verdict.Ambiguous})");
            }
        }

        // A branch created or deleted outside GrayMoon reports the branch lists and nothing else. It is not a completed
        // sync: it must not derive sync status, look up pull requests or recompute dependencies for a checkout it never read.
        var refsOnly = snapshot.IsBranchRefsOnly;
        var written = await stateWriter.ApplyAsync(contextId.Value, n.WorkspaceId, n.RepositoryId, snapshot, new RepositoryStateWriteOptions
        {
            SyncStatus = refsOnly ? SyncStatusWrite.Leave : SyncStatusWrite.Derive,
            ErrorMessageForcesInSync = true,
            // A just-created Feature worktree is on a new local branch: it has no pull request to find.
            ReconcilePullRequest = !n.FreshWorktree && !refsOnly,
        });
        // The context or the link was deleted while the notification waited: nothing was stored, nothing follows.
        if (!written.Applied)
            return;

        // From here the repository's state is stored: the completion of this context covers it, whatever happens below.
        observer.StateWritten(new AppliedSync(
            n.WorkspaceId, contextId.Value, contextInfo.IsSpecialWorkspace, n.RepositoryId, NeedsRecompute: !refsOnly, written.Changed));

        var branchWriter = scope.ServiceProvider.GetRequiredService<RepositoryBranchWriter>();

        // Persist tags and compute HasNewerTag when the worker includes a tag list (checkout-to-tag sync).
        // Runs after the state write because it is what decides HasNewerTag, which the writer clears.
        if (n.RemoteTags != null)
        {
            await branchWriter.PersistAsync(
                wr.WorkspaceRepositoryId,
                localBranches: null,
                remoteBranches: null,
                defaultBranchName: null,
                tags: n.RemoteTags,
                cancellationToken: default);
            // The tag pin belongs to the context the hook reported for, not to the shared link.
            await stateWriter.ApplyTagRefreshAsync(contextId.Value, wr.WorkspaceRepositoryId, n.RemoteTags, n.Tag);
        }

        // Prune remote branches from DB that no longer exist in git (fetch --prune ran on the worker side).
        // The hook flow reports remotes without persisting a full branch list, so this cannot go through the
        // writer's branch-replace path.
        if (n.RemoteBranches != null && !snapshot.BranchesProbed)
        {
            var pruned = await branchWriter.PruneRemoteBranchesAsync(wr.WorkspaceRepositoryId, n.RemoteBranches);
            if (pruned > 0)
                logger.LogInformation("SyncCommand pruned {Count} stale remote branch(es) for repo {RepositoryId}", pruned, n.RepositoryId);
        }

        if (!refsOnly)
            await UpdateContextSyncFlagsAsync(dbContext, contextId.Value, n.WorkspaceId, contextInfo.IsSpecialWorkspace);

        if (!string.IsNullOrWhiteSpace(n.ErrorMessage))
            await hubContext.Clients.All.SendAsync("RepositoryError", n.WorkspaceId, n.RepositoryId, n.ErrorMessage);

        // D8: a hook sync of the Workspace repository in the special context re-checks the Workspace definition.
        if (!refsOnly && contextInfo.IsSpecialWorkspace && wr.Role == WorkspaceRepositoryRole.Workspace)
            WorkspaceManifestHooks.DetectDriftInBackground(scopeFactory, logger, n.WorkspaceId);

        logger.LogDebug(
            "SyncCommand persisted in {ElapsedMs}ms: workspace={WorkspaceId}, context={ContextId}, repo={RepositoryId}, version={Version}, branch={Branch}",
            totalSw.ElapsedMilliseconds, n.WorkspaceId, contextId.Value.Value, n.RepositoryId, n.Version, n.Branch);
    }

    /// <summary>True when the Feature finalization ended; false when the wait ran out and the sync is applied anyway.</summary>
    private async Task<bool> WaitForFinalizationAsync(FeatureFinalizationCoordinator coordinator, int contextId)
    {
        try
        {
            await coordinator.WaitForFinalizationAsync(contextId);
            return true;
        }
        catch (TimeoutException)
        {
            logger.LogWarning(
                "SyncCommand: Feature finalization of context {ContextId} did not finish in time; applying the sync anyway",
                contextId);
            return false;
        }
    }

    /// <inheritdoc />
    public async Task CompleteAsync(IReadOnlyList<AppliedSync> items)
    {
        if (items.Count == 0)
            return;

        var workspaceId = items[0].WorkspaceId;
        var contextId = items[0].ContextId;
        if (items.Any(i => i.WorkspaceId != workspaceId || i.ContextId != contextId))
            throw new ArgumentException("A completion covers the notifications of one context.", nameof(items));

        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var coalescer = scope.ServiceProvider.GetRequiredService<SyncRecomputeCoalescer>();
            var key = $"{workspaceId}:{contextId.Value}";

            // The recompute reads the checked-out commit, the version and the projects. A notification that changed
            // none of them (it confirmed the stored state, or changed commit counts, the upstream flag or branch
            // lists only) leaves every derived value as it is. Refs-only notifications never need it.
            var changed = RepositoryFactGroups.None;
            var unknown = false;
            foreach (var item in items.Where(i => i.NeedsRecompute))
            {
                if (item.Changed is { } groups)
                    changed |= groups;
                else
                    unknown = true;
            }

            var needed = unknown || WorkspaceStateRecomputeScope.NeedsRecompute(changed);
            // A recompute that failed earlier left the derived state behind the rows: this completion makes up for it.
            var owed = coalescer.TakeOwed(key);
            // So does a Workspace profile that changed since the last completion of this context: what is derived
            // (versions, version file values, dependency state) depends on it.
            var profile = await scope.ServiceProvider.GetRequiredService<GrayMoon.Application.Workspaces.IWorkspaceCapabilitiesResolver>().GetAsync(workspaceId);
            var profileChanged = coalescer.ObserveProfile(key, $"{profile.Type}|{profile.VersioningMode}|{profile.CiProvider}");
            owed |= profileChanged;
            if (needed || owed)
            {
                var recomputeSw = Stopwatch.StartNew();
                var recomputeScope = scope.ServiceProvider.GetRequiredService<WorkspaceStateRecomputeScope>();
                var complete = true;
                try
                {
                    // The recompute is a full rebuild from the stored rows, so one run covers every state write before it.
                    // The coalescer keeps it from overlapping the recompute of a commit sync on the same context.
                    await coalescer.RunAsync(key, async () => complete = await recomputeScope.RecomputeAsync(workspaceId, contextId));
                }
                catch
                {
                    coalescer.MarkOwed(key);
                    throw;
                }

                if (!complete)
                    coalescer.MarkOwed(key);
                logger.LogDebug(
                    "SyncCommand recompute for {Count} notification(s) took {ElapsedMs}ms: workspace={WorkspaceId}, context={ContextId}, reason={Reason}, changed={Changed}, complete={Complete}",
                    items.Count, recomputeSw.ElapsedMilliseconds, workspaceId, contextId.Value,
                    unknown ? "change not reported" : needed ? "inputs changed" : profileChanged ? "Workspace profile changed" : "owed after a failed recompute", changed, complete);
            }
            else
            {
                logger.LogDebug(
                    "SyncCommand recompute not needed for {Count} notification(s): workspace={WorkspaceId}, context={ContextId}, changed={Changed}",
                    items.Count, workspaceId, contextId.Value, changed);
            }
        }
        finally
        {
            // Sent also when the recompute threw: the state rows changed and the grid has no other trigger.
            await BroadcastAsync(items, workspaceId, contextId);
        }
    }

    /// <summary>
    /// The repository events first, then the context event once. Legacy WorkspaceSynced/RepositorySynced remain for the
    /// special Workspace so existing page listeners keep working during the Features cutover.
    /// </summary>
    private async Task BroadcastAsync(IReadOnlyList<AppliedSync> items, int workspaceId, WorkspaceFeatureContextId contextId)
    {
        var isSpecialWorkspace = items[0].IsSpecialWorkspace;
        // The only trace of a batch that needed no recompute (branch lists only), and of how often a page is told to reload.
        logger.LogDebug(
            "SyncCommand broadcast for {Count} notification(s): workspace={WorkspaceId}, context={ContextId}",
            items.Count, workspaceId, contextId.Value);
        foreach (var repositoryId in items.Select(i => i.RepositoryId).Distinct())
        {
            await hubContext.Clients.All.SendAsync("ContextRepositorySynced", workspaceId, contextId.Value, repositoryId);
            if (isSpecialWorkspace)
                await hubContext.Clients.All.SendAsync("RepositorySynced", workspaceId, repositoryId);
        }

        await hubContext.Clients.All.SendAsync("ContextSynced", workspaceId, contextId.Value);
        if (isSpecialWorkspace)
            await hubContext.Clients.All.SendAsync("WorkspaceSynced", workspaceId);
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
}
