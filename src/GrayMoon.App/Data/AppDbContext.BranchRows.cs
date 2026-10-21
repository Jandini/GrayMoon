using GrayMoon.App.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace GrayMoon.App.Data;

/// <summary>
/// Branch rows are written by several parallel writers (push, checkout, hook sync, fetch, workspace
/// restore). Each decides "this ref has no row yet" from an earlier read, so two of them can insert the
/// same <c>(WorkspaceRepositoryId, BranchName, IsRemote)</c>. The unique index rejects the later insert.
/// That row is already stored, so every save of this context drops it instead of failing the caller:
/// a lost race must never abort an unrelated operation such as a multi-level push.
/// </summary>
public partial class AppDbContext
{
    /// <summary>Bounds the retry loop so a writer that keeps colliding cannot spin forever.</summary>
    private const int MaxBranchRowSaveAttempts = 3;

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
        var dropped = 0;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return (await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken), dropped);
            }
            catch (DbUpdateException ex) when (attempt < MaxBranchRowSaveAttempts && IsUniqueConstraintViolation(ex))
            {
                // A failed save leaves every entity pending, so dropping the rows that are already stored
                // and saving again writes the rest unchanged. Nothing else is swallowed: a violation with
                // no stored branch row behind it is a real conflict and surfaces.
                var detached = await DetachAddedBranchRowsAlreadyStoredAsync(cancellationToken);
                if (detached == 0)
                    throw;

                dropped += detached;
            }
        }
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

    /// <summary>SQLite extended result SQLITE_CONSTRAINT_UNIQUE (2067), or the message EF surfaces when the numeric code is absent.</summary>
    private static bool IsUniqueConstraintViolation(Exception exception)
    {
        for (var current = exception; current != null; current = current.InnerException)
        {
            if (current is SqliteException sqlite
                && (sqlite.SqliteExtendedErrorCode == 2067
                    || sqlite.Message.Contains("UNIQUE constraint failed", StringComparison.Ordinal)))
                return true;
        }

        return false;
    }
}
