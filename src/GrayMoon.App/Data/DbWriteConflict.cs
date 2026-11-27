using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace GrayMoon.App.Data;

/// <summary>
/// For writers that read a row, decide from what they read (add it, update it, remove it) and then save, while
/// another writer may do the same to the same row on another context. The loser's save fails in one of two ways:
/// the row it loaded is gone (<see cref="DbUpdateConcurrencyException"/>), or the row it found missing was added
/// meanwhile (a unique index violation). Neither is an error of the operation: reading again shows what the other
/// writer stored, and deciding again from that gives the right result.
/// </summary>
/// <remarks>
/// <see cref="RetryAsync(Func{Task}, CancellationToken)"/> repeats the whole read-decide-save. The delegate must
/// therefore read again on every call: it creates its own <see cref="AppDbContext"/> from the factory, or clears
/// the tracker of the one it uses, before it reads. Anything else that fails is rethrown at once, and so is a
/// lost race that repeats <see cref="MaxAttempts"/> times.
/// </remarks>
public static class DbWriteConflict
{
    /// <summary>How often a read-decide-save is tried before a lost race is reported as a failure.</summary>
    public const int MaxAttempts = 3;

    // SQLITE_CONSTRAINT, and its extended codes for a unique index and a primary key.
    private const int SqliteConstraint = 19;
    private const int SqliteConstraintUnique = 2067;
    private const int SqliteConstraintPrimaryKey = 1555;

    /// <summary>True when another writer changed the same rows between this writer's read and its save.</summary>
    public static bool IsLostRace(Exception exception)
        => exception is DbUpdateConcurrencyException || IsUniqueViolation(exception);

    /// <summary>True when the save tried to insert a row whose unique key is already stored.</summary>
    public static bool IsUniqueViolation(Exception exception)
        => exception is DbUpdateException { InnerException: SqliteException { SqliteErrorCode: SqliteConstraint } sqlite }
           && sqlite.SqliteExtendedErrorCode is SqliteConstraintUnique or SqliteConstraintPrimaryKey;

    /// <summary>Runs <paramref name="readDecideSave"/> again when its save lost a race. See the remarks of the class.</summary>
    public static async Task RetryAsync(Func<Task> readDecideSave, CancellationToken cancellationToken = default)
    {
        await RetryAsync(
            async () =>
            {
                await readDecideSave();
                return true;
            },
            cancellationToken);
    }

    /// <inheritdoc cref="RetryAsync(Func{Task}, CancellationToken)"/>
    public static async Task<T> RetryAsync<T>(Func<Task<T>> readDecideSave, CancellationToken cancellationToken = default)
    {
        for (var attempt = 1; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return await readDecideSave();
            }
            catch (DbUpdateException ex) when (attempt < MaxAttempts && IsLostRace(ex))
            {
                // Another writer stored its rows first. Read again and decide from what is stored now.
            }
        }
    }
}
