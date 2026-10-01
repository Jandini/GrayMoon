using GrayMoon.App.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace GrayMoon.App.Tests;

public class MigrationsRunnerTests
{
    [Fact]
    public async Task RunAllAsync_is_idempotent_on_second_run_and_tolerant_when_schema_already_current()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();

        // Fresh db already has the current schema from EnsureCreated; the legacy baseline step must be a
        // tolerant no-op here, not throw. The highest version is LegacyBaselineVersion once no strict steps
        // are registered yet, and the highest strict step version once units like B2 add one.
        var highestExpectedVersion = Math.Max(
            Migrations.LegacyBaselineVersion,
            Migrations.StrictSteps.Count == 0 ? 0 : Migrations.StrictSteps.Max(s => s.Version));

        await Migrations.RunAllAsync(db, NullLogger.Instance);

        var versionAfterFirst = await GetUserVersionAsync(connection);
        Assert.Equal(highestExpectedVersion, versionAfterFirst);

        await Migrations.RunAllAsync(db, NullLogger.Instance);

        var versionAfterSecond = await GetUserVersionAsync(connection);
        Assert.Equal(highestExpectedVersion, versionAfterSecond);
    }

    [Fact]
    public async Task RunAllAsync_backs_up_the_database_before_the_first_pending_step_and_not_again_when_nothing_is_pending()
    {
        var dir = Path.Combine(Path.GetTempPath(), "gm-migrations-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var dbPath = Path.Combine(dir, "test.db");
        var connectionString = $"Data Source={dbPath}";
        try
        {
            var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connectionString).Options;
            await using (var db = new AppDbContext(options))
            {
                await db.Database.EnsureCreatedAsync();
                await Migrations.RunAllAsync(db, NullLogger.Instance);
            }

            var backupsAfterFirstRun = Directory.GetFiles(dir, "test.db.bak-*");
            Assert.Single(backupsAfterFirstRun);

            await using (var checkConn = new SqliteConnection($"Data Source={backupsAfterFirstRun[0]}"))
            {
                await checkConn.OpenAsync();
                await using var cmd = checkConn.CreateCommand();
                cmd.CommandText = "PRAGMA integrity_check;";
                var result = await cmd.ExecuteScalarAsync() as string;
                Assert.Equal("ok", result, ignoreCase: true);
            }

            await using (var db = new AppDbContext(options))
            {
                await Migrations.RunAllAsync(db, NullLogger.Instance);
            }

            var backupsAfterSecondRun = Directory.GetFiles(dir, "test.db.bak-*");
            Assert.Single(backupsAfterSecondRun);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort cleanup */ }
        }
    }

    [Fact]
    public async Task RunStrictStepAsync_failure_rolls_back_the_schema_change_and_the_version_and_throws()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();
        await db.Database.ExecuteSqlRawAsync("PRAGMA user_version = 5;");

        var ex = await Assert.ThrowsAsync<DatabaseMigrationException>(() =>
            Migrations.RunStrictStepAsync(
                db,
                6,
                "test-step",
                async _ =>
                {
                    await db.Database.ExecuteSqlRawAsync("ALTER TABLE Settings ADD COLUMN TestColumn TEXT NULL;");
                    throw new InvalidOperationException("boom");
                },
                @"C:\fake\test.db.bak-1",
                NullLogger.Instance));

        Assert.Equal(6, ex.Version);
        Assert.Equal("test-step", ex.StepName);
        Assert.Contains(@"test.db.bak-1", ex.Message, StringComparison.Ordinal);

        var versionAfter = await GetUserVersionAsync(connection);
        Assert.Equal(5, versionAfter);

        await using var colCmd = connection.CreateCommand();
        colCmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('Settings') WHERE name = 'TestColumn'";
        var columnCount = Convert.ToInt32(await colCmd.ExecuteScalarAsync());
        Assert.Equal(0, columnCount);
    }

    private static async Task<int> GetUserVersionAsync(SqliteConnection connection)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }
}
