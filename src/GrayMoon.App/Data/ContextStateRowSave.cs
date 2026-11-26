using GrayMoon.App.Models;
using Microsoft.EntityFrameworkCore;

namespace GrayMoon.App.Data;

/// <summary>
/// For the writers that store derived values on the per-context state row and create that row when a context has
/// none for a repository (the dependency stats, the file-version counters). The state writer creates the same row
/// on another scope, and the unique index on (context, link) lets only one insert through.
/// </summary>
public static class ContextStateRowSave
{
    /// <summary>
    /// Saves the context. When the save fails because a state row this context added was stored by another writer
    /// meanwhile, the unsaved row is given up, the stored row is loaded, <paramref name="copyOntoStoredRow"/> copies
    /// this writer's values from the unsaved row onto it, and the context is saved again; everything else that was
    /// pending goes with that save. The tracker is not cleared: these writers use the scope's context, and other
    /// services of the scope track rows in it. A unique violation that is not such a row is rethrown.
    /// </summary>
    /// <param name="copyOntoStoredRow">Receives the row that could not be added, then the stored row. Copy only what this writer owns.</param>
    public static async Task SaveAdoptingStoredStateRowsAsync(
        this AppDbContext dbContext,
        Action<WorkspaceRepositoryContextState, WorkspaceRepositoryContextState> copyOntoStoredRow,
        CancellationToken cancellationToken = default)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await dbContext.SaveChangesAsync(cancellationToken);
                return;
            }
            catch (DbUpdateException ex) when (attempt < DbWriteConflict.MaxAttempts && DbWriteConflict.IsUniqueViolation(ex))
            {
                var unsaved = dbContext.ChangeTracker.Entries<WorkspaceRepositoryContextState>()
                    .Where(entry => entry.State == EntityState.Added)
                    .Select(entry => entry.Entity)
                    .ToList();
                if (unsaved.Count == 0)
                    throw;

                var contextIds = unsaved.Select(s => s.WorkspaceFeatureContextId).Distinct().ToList();
                var linkIds = unsaved.Select(s => s.WorkspaceRepositoryId).Distinct().ToList();
                var stored = await dbContext.WorkspaceRepositoryContextStates
                    .Where(s => contextIds.Contains(s.WorkspaceFeatureContextId) && linkIds.Contains(s.WorkspaceRepositoryId))
                    .ToListAsync(cancellationToken);
                // The query also returns the unsaved rows it already tracks; only rows with a key are stored.
                var storedByKey = stored
                    .Where(s => dbContext.Entry(s).State != EntityState.Added)
                    .ToDictionary(s => (s.WorkspaceFeatureContextId, s.WorkspaceRepositoryId));

                var adopted = 0;
                foreach (var lost in unsaved)
                {
                    if (!storedByKey.TryGetValue((lost.WorkspaceFeatureContextId, lost.WorkspaceRepositoryId), out var row))
                        continue;

                    dbContext.Entry(lost).State = EntityState.Detached;
                    copyOntoStoredRow(lost, row);
                    adopted++;
                }

                // No row of this save was stored by someone else: the violation is something else.
                if (adopted == 0)
                    throw;
            }
        }
    }
}
