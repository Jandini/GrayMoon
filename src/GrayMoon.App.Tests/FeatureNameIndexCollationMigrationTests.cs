using GrayMoon.App.Data;
using GrayMoon.App.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace GrayMoon.App.Tests;

/// <summary>
/// E1 strict step: recreates IX_WorkspaceFeatures_WorkspaceId_Name with COLLATE NOCASE on Name so 'Foo'
/// and 'foo' count as the same name on upgraded databases, matching a fresh EnsureCreated() database.
/// Skipped (with a warning, never a startup failure) when a case-only duplicate already exists.
/// </summary>
public sealed class FeatureNameIndexCollationMigrationTests
{
    [Fact]
    public async Task Recreates_the_index_with_NOCASE_when_there_is_no_existing_duplicate()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = await CreateDatabaseWithCaseSensitiveFeatureNameIndexAsync(connection);

        var workspace = new Workspace { Name = "ws", RootPath = @"C:\ws" };
        db.Workspaces.Add(workspace);
        await db.SaveChangesAsync();
        db.WorkspaceFeatures.Add(new WorkspaceFeature { WorkspaceId = workspace.WorkspaceId, Name = "feature/one" });
        await db.SaveChangesAsync();

        await Migrations.MigrateFeatureNameIndexCollationAsync(db);

        var indexSql = await ReadIndexSqlAsync(connection, "IX_WorkspaceFeatures_WorkspaceId_Name");
        Assert.Contains("NOCASE", indexSql, StringComparison.OrdinalIgnoreCase);

        // The recreated index now enforces case-insensitive uniqueness at the database level.
        db.WorkspaceFeatures.Add(new WorkspaceFeature { WorkspaceId = workspace.WorkspaceId, Name = "FEATURE/ONE" });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Keeps_the_old_index_and_does_not_throw_when_a_case_only_duplicate_already_exists()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = await CreateDatabaseWithCaseSensitiveFeatureNameIndexAsync(connection);

        var workspace = new Workspace { Name = "ws", RootPath = @"C:\ws" };
        db.Workspaces.Add(workspace);
        await db.SaveChangesAsync();
        db.WorkspaceFeatures.Add(new WorkspaceFeature { WorkspaceId = workspace.WorkspaceId, Name = "Dup" });
        db.WorkspaceFeatures.Add(new WorkspaceFeature { WorkspaceId = workspace.WorkspaceId, Name = "dup" });
        await db.SaveChangesAsync();

        await Migrations.MigrateFeatureNameIndexCollationAsync(db);

        var indexSql = await ReadIndexSqlAsync(connection, "IX_WorkspaceFeatures_WorkspaceId_Name");
        Assert.DoesNotContain("NOCASE", indexSql, StringComparison.OrdinalIgnoreCase);

        var count = await db.WorkspaceFeatures.CountAsync(f => f.WorkspaceId == workspace.WorkspaceId);
        Assert.Equal(2, count);
    }

    [Fact]
    public async Task Running_the_step_twice_is_a_no_op_the_second_time()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = await CreateDatabaseWithCaseSensitiveFeatureNameIndexAsync(connection);

        var workspace = new Workspace { Name = "ws", RootPath = @"C:\ws" };
        db.Workspaces.Add(workspace);
        await db.SaveChangesAsync();
        db.WorkspaceFeatures.Add(new WorkspaceFeature { WorkspaceId = workspace.WorkspaceId, Name = "feature/one" });
        await db.SaveChangesAsync();

        await Migrations.MigrateFeatureNameIndexCollationAsync(db);
        await Migrations.MigrateFeatureNameIndexCollationAsync(db);

        var indexSql = await ReadIndexSqlAsync(connection, "IX_WorkspaceFeatures_WorkspaceId_Name");
        Assert.Contains("NOCASE", indexSql, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<AppDbContext> CreateDatabaseWithCaseSensitiveFeatureNameIndexAsync(SqliteConnection connection)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();

        // Simulates a database created before E1: the Name column (and so the index on it) has the
        // database's default BINARY collation, not NOCASE. EnsureCreatedAsync() builds the current (NOCASE)
        // model, so the table is empty at this point and safe to drop and recreate with the pre-E1 shape.
        await ExecuteAsync(connection, """DROP TABLE "WorkspaceFeatures";""");
        await ExecuteAsync(connection, """
            CREATE TABLE "WorkspaceFeatures" (
                "WorkspaceFeatureId" INTEGER NOT NULL CONSTRAINT "PK_WorkspaceFeatures" PRIMARY KEY AUTOINCREMENT,
                "WorkspaceId" INTEGER NOT NULL,
                "Name" TEXT NOT NULL,
                "LifecycleState" INTEGER NOT NULL,
                "BaseKind" INTEGER NOT NULL,
                "BaseWorkspaceFeatureId" INTEGER NULL,
                "CreatedAt" TEXT NOT NULL,
                "UpdatedAt" TEXT NOT NULL,
                "LastError" TEXT NULL,
                CONSTRAINT "FK_WorkspaceFeatures_Workspaces_WorkspaceId" FOREIGN KEY ("WorkspaceId") REFERENCES "Workspaces" ("WorkspaceId") ON DELETE CASCADE,
                CONSTRAINT "FK_WorkspaceFeatures_WorkspaceFeatures_BaseWorkspaceFeatureId" FOREIGN KEY ("BaseWorkspaceFeatureId") REFERENCES "WorkspaceFeatures" ("WorkspaceFeatureId") ON DELETE RESTRICT
            );
            """);
        await ExecuteAsync(connection, """
            CREATE UNIQUE INDEX "IX_WorkspaceFeatures_WorkspaceId_Name"
            ON "WorkspaceFeatures" ("WorkspaceId", "Name");
            """);

        return db;
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<string> ReadIndexSqlAsync(SqliteConnection connection, string indexName)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT sql FROM sqlite_master WHERE type = 'index' AND name = $name";
        var param = cmd.CreateParameter();
        param.ParameterName = "$name";
        param.Value = indexName;
        cmd.Parameters.Add(param);
        var result = await cmd.ExecuteScalarAsync() as string;
        return result ?? string.Empty;
    }
}
