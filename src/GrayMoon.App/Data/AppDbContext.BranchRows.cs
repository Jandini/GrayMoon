using GrayMoon.App.Models;
using Microsoft.EntityFrameworkCore;

namespace GrayMoon.App.Data;

/// <summary>
/// Branch rows are written by several parallel writers (push, checkout, hook sync, fetch, workspace
/// restore). Each decides "this ref has no row yet" from an earlier read, so two of them can insert the
/// same <c>(WorkspaceRepositoryId, BranchName, IsRemote)</c>, and the unique index would reject the later insert.
/// That row is already stored, so every save of this context drops it instead of failing the caller:
/// a lost race must never abort an unrelated operation such as a multi-level push.
/// <para>
/// A save that adds branch rows takes the database write lock first (a transaction EF Core opens on SQLite is
/// <c>BEGIN IMMEDIATE</c>), then reads which of its added rows are stored, drops those, and inserts the rest
/// before it commits. No other writer can commit between that read and the insert, so the insert of a row that
/// is already stored is never sent. A unique violation that still occurs is a real conflict and surfaces.
/// </para>
/// </summary>
public partial class AppDbContext
{
    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        var (saved, _) = await SaveDroppingStoredBranchRowsAsync(acceptAllChangesOnSuccess, cancellationToken);
        return saved;
    }

    /// <summary>
    /// Saves like <see cref="DbContext.SaveChangesAsync(CancellationToken)"/> and returns how many added branch rows were
    /// dropped because another writer had already stored them. Callers that report "rows inserted" subtract it.
    /// </summary>
    public async Task<int> SaveChangesCountingStoredBranchRowsAsync(CancellationToken cancellationToken = default)
    {
        var (_, dropped) = await SaveDroppingStoredBranchRowsAsync(acceptAllChangesOnSuccess: true, cancellationToken);
        return dropped;
    }

    private async Task<(int Saved, int Dropped)> SaveDroppingStoredBranchRowsAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken)
    {
        KeepWorkspaceRepositoryWritable();
        if (!ChangeTracker.Entries<RepositoryBranch>().Any(entry => entry.State == EntityState.Added))
            return (await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken), 0);

        // The caller decided "no row yet" from an earlier read. The check is repeated here under the write lock.
        // Without the lock another writer could commit the same row between the check and the insert; the insert
        // then failed, EF Core logged it as two errors with a stack trace, and the save was repeated
        // (App logs of 2026-11-25 and 2026-11-26).
        if (Database.CurrentTransaction != null)
        {
            // The caller's transaction already holds the write lock.
            var droppedInCallerTransaction = await DetachAddedBranchRowsAlreadyStoredAsync(cancellationToken);
            return (await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken), droppedInCallerTransaction);
        }

        await using var transaction = await Database.BeginTransactionAsync(cancellationToken);
        var dropped = await DetachAddedBranchRowsAlreadyStoredAsync(cancellationToken);
        // Accepted only after the commit, so a commit that fails leaves every change pending.
        var saved = await base.SaveChangesAsync(acceptAllChangesOnSuccess: false, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        if (acceptAllChangesOnSuccess)
            ChangeTracker.AcceptAllChanges();
        return (saved, dropped);
    }

    /// <summary>Detaches added branch rows whose unique key is already committed. Returns how many were detached.</summary>
    private async Task<int> DetachAddedBranchRowsAlreadyStoredAsync(CancellationToken cancellationToken)
    {
        var added = ChangeTracker.Entries<RepositoryBranch>()
            .Where(entry => entry.State == EntityState.Added)
            .Select(entry => entry.Entity)
            .ToList();
        if (added.Count == 0)
            return 0;

        var linkIds = added.Select(branch => branch.WorkspaceRepositoryId).Distinct().ToList();
        var stored = await RepositoryBranches
            .AsNoTracking()
            .Where(branch => linkIds.Contains(branch.WorkspaceRepositoryId))
            .Select(branch => new { branch.WorkspaceRepositoryId, branch.BranchName, branch.IsRemote })
            .ToListAsync(cancellationToken);
        var storedKeys = stored
            .Select(branch => (branch.WorkspaceRepositoryId, branch.BranchName, branch.IsRemote))
            .ToHashSet();

        var detached = 0;
        foreach (var branch in added)
        {
            if (!storedKeys.Contains((branch.WorkspaceRepositoryId, branch.BranchName, branch.IsRemote)))
                continue;

            Entry(branch).State = EntityState.Detached;
            detached++;
        }

        return detached;
    }
}
