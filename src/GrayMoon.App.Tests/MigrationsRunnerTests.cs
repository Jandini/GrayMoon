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
        await db.Database.CreateSchemaAsync();

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
                await db.Database.CreateSchemaAsync();
                // A database with no rows is not backed up, so give this one something to lose.
                await db.Database.ExecuteSqlRawAsync("INSERT INTO Settings (Key, Value) VALUES ('seed', 'value');");
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
    public async Task RunAllAsync_does_not_back_up_a_brand_new_database_with_no_rows()
    {
        var dir = Path.Combine(Path.GetTempPath(), "gm-migrations-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var dbPath = Path.Combine(dir, "test.db");
        try
        {
            var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite($"Data Source={dbPath};Pooling=False").Options;
            await using (var db = new AppDbContext(options))
            {
                await db.Database.CreateSchemaAsync();
                await Migrations.RunAllAsync(db, NullLogger.Instance);
            }

            Assert.Empty(Directory.GetFiles(dir, "test.db.bak-*"));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort cleanup */ }
        }
    }

    [Fact]
    public async Task RunAllAsync_stops_before_any_step_when_the_backup_cannot_be_written_and_names_the_path()
    {
        // Review 2026-11-10 finding 17: a failed VACUUM INTO (disk full, permissions) must stop the migration
        // with a message naming the path, not surface a raw SqliteException or run steps without a backup.
        var dir = Path.Combine(Path.GetTempPath(), "gm-migrations-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var dbPath = Path.Combine(dir, "test.db");
        var clock = new ManualClock(new DateTimeOffset(2026, 11, 10, 8, 30, 15, TimeSpan.Zero));
        // The backup target is occupied by a folder, so SQLite cannot write the file there.
        var backupPath = Path.Combine(dir, "test.db.bak-20261110-083015");
        Directory.CreateDirectory(backupPath);
        try
        {
            var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite($"Data Source={dbPath};Pooling=False").Options;
            await using var db = new AppDbContext(options);
            await db.Database.CreateSchemaAsync();
            await db.Database.ExecuteSqlRawAsync("INSERT INTO Settings (Key, Value) VALUES ('seed', 'value');");

            var ex = await Assert.ThrowsAsync<DatabaseMigrationException>(() => Migrations.RunAllAsync(db, NullLogger.Instance, clock));

            Assert.Contains(backupPath, ex.Message, StringComparison.Ordinal);
            Assert.Equal(backupPath, ex.BackupPath);
            Assert.NotNull(ex.InnerException);

            // No step ran: the version is still 0 and the seeded row is intact.
            var connection = (SqliteConnection)db.Database.GetDbConnection();
            Assert.Equal(0, await GetUserVersionAsync(connection));
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT Value FROM Settings WHERE Key = 'seed'";
            Assert.Equal("value", await cmd.ExecuteScalarAsync() as string);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort cleanup */ }
        }
    }

    [Fact]
    public async Task RunTolerantLegacyStepAsync_swallows_the_failure_and_drops_entities_the_step_left_added()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var db = new AppDbContext(options);
        await db.Database.CreateSchemaAsync();

        await Migrations.RunTolerantLegacyStepAsync(
            db,
            "test-step",
            () =>
            {
                db.Workspaces.Add(new GrayMoon.App.Models.Workspace { Name = "half-added" });
                throw new InvalidOperationException("boom");
            },
            NullLogger.Instance);

        Assert.Empty(db.ChangeTracker.Entries());
        await db.SaveChangesAsync();
        Assert.False(await db.Workspaces.AnyAsync());
    }

    [Fact]
    public async Task Feature_context_schema_step_failure_leaves_no_added_entities_for_a_later_step_to_flush()
    {
        // The backfill adds context states and then saves. Without the GitVersionPending column that save
        // fails; the tolerant catch must clear the tracker or a later step's SaveChanges retries the insert.
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var db = new AppDbContext(options);
        await db.Database.CreateSchemaAsync();
        await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = OFF;");

        var connector = new GrayMoon.App.Models.Connector { ConnectorName = "c", ConnectorType = GrayMoon.App.Models.ConnectorType.GitHub, ApiBaseUrl = "https://api.github.com", IsActive = true };
        db.Connectors.Add(connector);
        await db.SaveChangesAsync();
        var workspace = new GrayMoon.App.Models.Workspace { Name = "ws" };
        db.Workspaces.Add(workspace);
        var repository = new GrayMoon.App.Models.Repository { ConnectorId = connector.ConnectorId, RepositoryName = "r", OrgName = "acme", CloneUrl = "https://example/r.git" };
        db.Repositories.Add(repository);
        await db.SaveChangesAsync();
        db.WorkspaceRepositories.Add(new GrayMoon.App.Models.WorkspaceRepositoryLink { WorkspaceId = workspace.WorkspaceId, RepositoryId = repository.RepositoryId });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE WorkspaceRepositoryContextStates DROP COLUMN GitVersionPending;");

        await Migrations.MigrateWorkspaceFeatureContextSchemaAsync(db, NullLogger.Instance);

        Assert.Empty(db.ChangeTracker.Entries());
    }

    [Fact]
    public async Task RunStrictStepAsync_failure_rolls_back_the_schema_change_and_the_version_and_throws()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var db = new AppDbContext(options);
        await db.Database.CreateSchemaAsync();
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
