using GrayMoon.App.Data;
using GrayMoon.App.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace GrayMoon.App.Tests;

/// <summary>Strict step 21 (plan package C, T32, T33): four nullable context state columns and the branch baseline table.</summary>
public sealed class EffectiveVersionMigrationTests
{
    private static readonly string[] Columns = ["EffectiveVersion", "EffectiveVersionKind", "EffectiveVersionCommitSha", "EffectiveVersionReasonCode"];

    [Fact]
    public async Task A_fresh_database_has_the_columns_the_table_and_the_unique_index_and_the_step_is_idempotent()
    {
        var (connection, db) = await CreateDbAsync();
        await using var _ = connection;
        await using var __ = db;

        await Migrations.MigrateEffectiveVersionsAsync(db, NullLogger.Instance);
        await Migrations.MigrateEffectiveVersionsAsync(db, NullLogger.Instance);

        Assert.All(Columns, column => Assert.Equal(1, Count(connection, $"SELECT COUNT(*) FROM pragma_table_info('WorkspaceRepositoryContextStates') WHERE name = '{column}'")));
        Assert.Equal(1, Count(connection, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'WorkspaceRepositoryBranchBaselines'"));
        Assert.Equal(1, Count(connection, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = 'IX_WorkspaceRepositoryBranchBaselines_Context_Repo_Branch' AND sql LIKE '%UNIQUE%'"));
    }

    [Fact]
    public async Task A_version_20_database_upgrades_and_keeps_its_data()
    {
        var (connection, db) = await CreateDbAsync();
        await using var _ = connection;
        await using var __ = db;
        var (contextId, linkId) = await SeedStateAsync(db);
        await DowngradeToVersion20Async(connection);

        var step = Migrations.StrictSteps.Single(s => s.Version == 21);
        await Migrations.RunStrictStepAsync(db, step.Version, step.Name, step.Action, null, NullLogger.Instance);

        Assert.Equal(21, Count(connection, "PRAGMA user_version;"));
        db.ChangeTracker.Clear();
        var state = await db.WorkspaceRepositoryContextStates.AsNoTracking().SingleAsync(s => s.WorkspaceFeatureContextId == contextId && s.WorkspaceRepositoryId == linkId);
        Assert.Equal("1.2.3", state.GitVersion);
        Assert.Equal("abc", state.HeadCommit);
        Assert.Null(state.EffectiveVersion);
        Assert.Null(state.EffectiveVersionKind);

        db.WorkspaceRepositoryBranchBaselines.Add(new WorkspaceRepositoryBranchBaseline
        {
            WorkspaceFeatureContextId = contextId,
            WorkspaceRepositoryId = linkId,
            BranchName = "feature/x",
            ParentBranchName = "main",
            BaseCommitSha = "abc",
            CreatedAtUtc = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        db.WorkspaceRepositoryBranchBaselines.Add(new WorkspaceRepositoryBranchBaseline
        {
            WorkspaceFeatureContextId = contextId,
            WorkspaceRepositoryId = linkId,
            BranchName = "feature/x",
            ParentBranchName = "main",
            BaseCommitSha = "def",
            CreatedAtUtc = DateTime.UtcNow,
        });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task The_runner_adds_the_columns_before_older_steps_insert_context_state_rows()
    {
        var (connection, db) = await CreateDbAsync();
        await using var _ = connection;
        await using var __ = db;
        await SeedStateAsync(db);
        await DowngradeToVersion20Async(connection);
        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "PRAGMA user_version = 15;";
            await cmd.ExecuteNonQueryAsync();
        }

        await Migrations.RunAllAsync(db, NullLogger.Instance, null, _ => Task.FromResult(true));

        Assert.Equal(Migrations.StrictSteps.Max(s => s.Version), Count(connection, "PRAGMA user_version;"));
        Assert.All(Columns, column => Assert.Equal(1, Count(connection, $"SELECT COUNT(*) FROM pragma_table_info('WorkspaceRepositoryContextStates') WHERE name = '{column}'")));
    }

    [Fact]
    public async Task A_failing_step_rolls_back_and_throws()
    {
        var (connection, db) = await CreateDbAsync();
        await using var _ = connection;
        await using var __ = db;
        await DowngradeToVersion20Async(connection);
        // Something that is not a table already holds the name, so CREATE TABLE IF NOT EXISTS fails after the columns were added.
        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "CREATE VIEW WorkspaceRepositoryBranchBaselines AS SELECT 1 AS x;";
            await cmd.ExecuteNonQueryAsync();
        }

        var step = Migrations.StrictSteps.Single(s => s.Version == 21);
        var ex = await Assert.ThrowsAsync<DatabaseMigrationException>(() =>
            Migrations.RunStrictStepAsync(db, step.Version, step.Name, step.Action, @"C:\fake\graymoon.db.bak-1", NullLogger.Instance));

        Assert.Equal(21, ex.Version);
        Assert.Equal(20, Count(connection, "PRAGMA user_version;"));
        Assert.All(Columns, column => Assert.Equal(0, Count(connection, $"SELECT COUNT(*) FROM pragma_table_info('WorkspaceRepositoryContextStates') WHERE name = '{column}'")));
    }

    private static async Task<(SqliteConnection Connection, AppDbContext Db)> CreateDbAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        await db.Database.CreateSchemaAsync();
        return (connection, db);
    }

    private static async Task<(int ContextId, int LinkId)> SeedStateAsync(AppDbContext db)
    {
        var connector = new Connector { ConnectorName = "gh", ConnectorType = ConnectorType.GitHub, ApiBaseUrl = "https://api.github.com", IsActive = true, IsHealthy = true };
        var workspace = new Workspace { Name = "ws" };
        db.Connectors.Add(connector);
        db.Workspaces.Add(workspace);
        await db.SaveChangesAsync();
        var repository = new Repository { ConnectorId = connector.ConnectorId, RepositoryName = "r", OrgName = "acme", CloneUrl = "https://example/r.git" };
        db.Repositories.Add(repository);
        await db.SaveChangesAsync();
        var link = new WorkspaceRepositoryLink { WorkspaceId = workspace.WorkspaceId, RepositoryId = repository.RepositoryId };
        var context = new WorkspaceFeatureContext { WorkspaceId = workspace.WorkspaceId, Kind = WorkspaceFeatureContextKind.Workspace, CreatedAt = DateTime.UtcNow };
        db.WorkspaceRepositories.Add(link);
        db.WorkspaceFeatureContexts.Add(context);
        await db.SaveChangesAsync();
        db.WorkspaceRepositoryContextStates.Add(new WorkspaceRepositoryContextState
        {
            WorkspaceFeatureContextId = context.WorkspaceFeatureContextId,
            WorkspaceRepositoryId = link.WorkspaceRepositoryId,
            GitVersion = "1.2.3",
            HeadCommit = "abc",
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return (context.WorkspaceFeatureContextId, link.WorkspaceRepositoryId);
    }

    private static async Task DowngradeToVersion20Async(SqliteConnection connection)
    {
        foreach (var sql in Columns.Select(c => $"ALTER TABLE WorkspaceRepositoryContextStates DROP COLUMN {c};")
            .Append("DROP TABLE IF EXISTS WorkspaceRepositoryBranchBaselines;")
            .Append("PRAGMA user_version = 20;"))
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = sql;
            await cmd.ExecuteNonQueryAsync();
        }
    }

    private static int Count(SqliteConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt32(cmd.ExecuteScalar());
    }
}
