using GrayMoon.App.Models;
using Microsoft.EntityFrameworkCore;

namespace GrayMoon.App.Repositories;

public sealed partial class WorkspaceProjectRepository
{
    /// <summary>
    /// The one reconciliation entry point for project snapshots of a context: merges the projects of every repository whose
    /// scan was authoritative (<see cref="ProjectScanOutcome.IsAuthoritative"/>), rebuilds those repositories' dependency
    /// edges (optional) and, when requested, recomputes the dependency levels, all under one projection gate and one database
    /// transaction. Failed or unscanned repositories are never touched, so a failed scan cannot remove projects or edges. If
    /// anything throws, the whole projection rolls back, including changes the caller staged on the shared DbContext and
    /// that this call would have saved (a projects-only call is a single save and is atomic by itself). Worker discovery happens before this call, never inside it. Joins a transaction the
    /// caller already opened. Re-running with the same outcomes changes nothing.
    /// </summary>
    /// <param name="mergeEdges">False for callers that merge the whole graph themselves right after (a batched sync).</param>
    public async Task ReconcileProjectSnapshotAsync(
        int workspaceId,
        int workspaceFeatureContextId,
        IReadOnlyList<ProjectScanOutcome> outcomes,
        bool mergeEdges,
        bool persistDependencyLevel,
        CancellationToken cancellationToken = default)
    {
        var authoritative = outcomes.Where(o => o.IsAuthoritative).GroupBy(o => o.RepositoryId).Select(g => g.Last()).ToList();
        var failed = outcomes.Count - authoritative.Count;
        if (failed > 0)
            logger.LogWarning(
                "Project reconciliation: WorkspaceId={WorkspaceId}, ContextId={ContextId}, {Failed} of {Total} repositories had no authoritative scan; their projects and edges were left unchanged",
                workspaceId, workspaceFeatureContextId, failed, outcomes.Count);
        if (authoritative.Count == 0)
            return;

        using var projectionGate = await AcquireProjectionGateAsync(workspaceFeatureContextId, cancellationToken);

        // Projects-only is one SaveChanges, already atomic in EF's own short transaction; an explicit one is for the multi-save projection
        // (projects, then edges), and is kept off the single-save path because SQLite shared-cache table locks make a long transaction
        // fail concurrent writers of the same tables instead of waiting for them.
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var ownsTransaction = mergeEdges && dbContext.Database.CurrentTransaction is null;
        await using var transaction = ownsTransaction ? await dbContext.Database.BeginTransactionAsync(cancellationToken) : null;
        try
        {
            await MergeProjectsBatchCoreAsync(
                workspaceId,
                authoritative.Select(o => (o.RepositoryId, o.Projects)).ToList(),
                workspaceFeatureContextId,
                cancellationToken);

            if (mergeEdges)
            {
                await MergeDependenciesCoreAsync(
                    workspaceId,
                    authoritative.Select(o => (o.RepositoryId, (IReadOnlyList<SyncProjectInfo>?)o.Projects)).ToList(),
                    workspaceFeatureContextId,
                    persistDependencyLevel,
                    cancellationToken);
            }

            if (transaction is not null)
                await transaction.CommitAsync(cancellationToken);

            logger.LogInformation(
                "Project reconciliation: WorkspaceId={WorkspaceId}, ContextId={ContextId}, Repositories={Repositories}, Skipped={Skipped}, Projects={Projects}, EdgesMerged={EdgesMerged}, ElapsedMs={ElapsedMs}",
                workspaceId, workspaceFeatureContextId, authoritative.Count, failed, authoritative.Sum(o => o.Projects.Count), mergeEdges, stopwatch.ElapsedMilliseconds);
        }
        catch
        {
            // The rolled-back entities are still tracked as saved; drop them so the context reloads the committed state.
            if (ownsTransaction)
                dbContext.ChangeTracker.Clear();
            throw;
        }
    }
}
