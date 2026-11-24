using GrayMoon.App.Data;
using GrayMoon.App.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace GrayMoon.App.Tests;

/// <summary>Strict step 22 adds <c>WorkspaceFileLineStatuses.NotCheckedReason</c> to a database that predates it.</summary>
public sealed class FileVersionNotCheckedMigrationTests
{
    [Fact]
    public async Task A_version_21_database_gets_the_column_and_keeps_its_rows()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        await db.Database.CreateSchemaAsync();
        await ExecuteAsync(connection, """
            INSERT INTO "WorkspaceFileLineStatuses" ("WorkspaceId", "RepositoryId", "FilePath", "FileName", "TokenName", "CurrentValue", "ExpectedValue", "TotalMatchedLines", "OutOfDateLines")
            VALUES (1, 2, 'version.props', 'version.props', '@api:commit', 'aaa', 'bbb', 0, 0)
            """);
        await ExecuteAsync(connection, """ALTER TABLE "WorkspaceFileLineStatuses" DROP COLUMN "NotCheckedReason" """);
        await ExecuteAsync(connection, "PRAGMA user_version = 21;");

        var step = Migrations.StrictSteps.Single(s => s.Version == 22);
        await Migrations.RunStrictStepAsync(db, step.Version, step.Name, step.Action, null, NullLogger.Instance);
        // Idempotent: a brand-new database already has the column when the step runs.
        await step.Action(db);

        Assert.Equal(22L, await ScalarAsync(connection, "PRAGMA user_version;"));
        var row = await db.WorkspaceFileLineStatuses.AsNoTracking().SingleAsync();
        Assert.Equal("bbb", row.ExpectedValue);
        Assert.Null(row.NotCheckedReason);

        db.WorkspaceFileLineStatuses.Add(new WorkspaceFileLineStatus
        {
            WorkspaceId = 1,
            RepositoryId = 3,
            FilePath = "version.props",
            FileName = "version.props",
            TokenName = "@api:commit",
            NotCheckedReason = "No commit is recorded for api. Sync the repository.",
        });
        await db.SaveChangesAsync();
        Assert.Equal(1L, await ScalarAsync(connection, """SELECT COUNT(*) FROM "WorkspaceFileLineStatuses" WHERE "NotCheckedReason" IS NOT NULL"""));
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> ScalarAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)(await command.ExecuteScalarAsync())!;
    }
}
