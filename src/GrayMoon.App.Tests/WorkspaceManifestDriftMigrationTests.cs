using GrayMoon.App.Data;
using GrayMoon.App.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace GrayMoon.App.Tests;

public sealed class WorkspaceManifestDriftMigrationTests
{
    private static async Task<(SqliteConnection Connection, AppDbContext Db)> CreateDbAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();
        return (connection, db);
    }

    [Fact]
    public async Task Column_added_once_and_idempotent()
    {
        var (connection, db) = await CreateDbAsync();
        await using var _ = connection;
        await using var __ = db;

        db.Workspaces.Add(new Workspace { Name = "ws" });
        await db.SaveChangesAsync();
        await DropColumnAsync(connection);

        Assert.Equal(0, await ColumnCountAsync(connection));

        await Migrations.MigrateWorkspaceManifestDriftColumnAsync(db, NullLogger.Instance);
        await Migrations.MigrateWorkspaceManifestDriftColumnAsync(db, NullLogger.Instance);

        Assert.Equal(1, await ColumnCountAsync(connection));

        db.ChangeTracker.Clear();
        var workspace = await db.Workspaces.AsNoTracking().SingleAsync();
        Assert.Null(workspace.ManifestDriftDetectedAt);

        var stamp = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        var tracked = await db.Workspaces.SingleAsync();
        tracked.ManifestDriftDetectedAt = stamp;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        Assert.Equal(stamp, (await db.Workspaces.AsNoTracking().SingleAsync()).ManifestDriftDetectedAt);
    }

    [Fact]
    public async Task Strict_step_6_is_registered_after_step_5()
    {
        var (connection, db) = await CreateDbAsync();
        await using var _ = connection;
        await using var __ = db;

        var step = Migrations.StrictSteps.Single(s => s.Version == 6);
        Assert.Contains(Migrations.StrictSteps, s => s.Version == 5);

        await DropColumnAsync(connection);
        await step.Action(db);

        Assert.Equal(1, await ColumnCountAsync(connection));
    }

    private static async Task DropColumnAsync(SqliteConnection connection)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "ALTER TABLE \"Workspaces\" DROP COLUMN \"ManifestDriftDetectedAt\"";
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<int> ColumnCountAsync(SqliteConnection connection)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('Workspaces') WHERE name = 'ManifestDriftDetectedAt'";
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }
}
