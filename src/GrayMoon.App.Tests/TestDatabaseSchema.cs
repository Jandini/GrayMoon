using System.Data;
using GrayMoon.App.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace GrayMoon.App.Tests;

/// <summary>
/// Building the schema from the EF model costs about 100 ms (every table, index and foreign key), and roughly five
/// hundred tests each used to do it for their own throwaway database. It is built once per test run into a template
/// database, and each test restores a copy with SQLite's backup API, which takes about a millisecond. A test still owns
/// a fully independent database. New tests should call <see cref="CreateSchemaAsync"/> (or
/// <see cref="CopyInto"/> when they hold the connection) instead of <c>EnsureCreatedAsync</c>.
/// </summary>
internal static class TestDatabaseSchema
{
    private static readonly object Gate = new();

    private static readonly Lazy<SqliteConnection> Template = new(() =>
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var context = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        context.Database.EnsureCreated();
        return connection;
    });

    /// <summary>Restores the current schema into an open, empty database. A connection is not thread-safe, so copies are serialized.</summary>
    public static void CopyInto(SqliteConnection target)
    {
        lock (Gate)
        {
            Template.Value.BackupDatabase(target);
        }
    }

    /// <summary>
    /// Same result as <c>EnsureCreatedAsync</c>, without rebuilding the schema when the context already has an open,
    /// empty SQLite connection to restore into. Any other situation falls back to <c>EnsureCreatedAsync</c>.
    /// </summary>
    public static async Task CreateSchemaAsync(this DatabaseFacade database)
    {
        if (database.GetDbConnection() is SqliteConnection { State: ConnectionState.Open } connection && IsEmpty(connection))
        {
            CopyInto(connection);
            return;
        }

        await database.EnsureCreatedAsync();
    }

    private static bool IsEmpty(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master";
        return Convert.ToInt32(command.ExecuteScalar()) == 0;
    }
}
