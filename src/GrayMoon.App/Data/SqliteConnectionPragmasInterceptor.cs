using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace GrayMoon.App.Data;

/// <summary>
/// Applies SQLite pragmas that are not stored in the database file. Only <c>journal_mode</c> persists;
/// <c>busy_timeout</c>, <c>foreign_keys</c> and <c>synchronous</c> are per connection (a new connection
/// starts at <c>synchronous=FULL</c>), so a startup PRAGMA on one connection does not cover later pooled or
/// factory connections. <c>NORMAL</c> is safe with WAL: a power loss can lose the last commits, never corrupt.
/// </summary>
public sealed class SqliteConnectionPragmasInterceptor : DbConnectionInterceptor
{
    public static readonly SqliteConnectionPragmasInterceptor Instance = new();

    public const int BusyTimeoutMilliseconds = 5000;

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        Apply(connection);
    }

    public override async Task ConnectionOpenedAsync(
        DbConnection connection,
        ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        await ApplyAsync(connection, cancellationToken);
    }

    private static void Apply(DbConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA busy_timeout=" + BusyTimeoutMilliseconds + ";";
        command.ExecuteNonQuery();
        command.CommandText = "PRAGMA foreign_keys=ON;";
        command.ExecuteNonQuery();
        command.CommandText = "PRAGMA synchronous=NORMAL;";
        command.ExecuteNonQuery();
    }

    private static async Task ApplyAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA busy_timeout=" + BusyTimeoutMilliseconds + ";";
        await command.ExecuteNonQueryAsync(cancellationToken);
        command.CommandText = "PRAGMA foreign_keys=ON;";
        await command.ExecuteNonQueryAsync(cancellationToken);
        command.CommandText = "PRAGMA synchronous=NORMAL;";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
