using GrayMoon.Abstractions.Workspaces;
using GrayMoon.App.Data;
using GrayMoon.App.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace GrayMoon.App.Tests;

public sealed class WorkspaceProfileMigrationTests
{
    [Fact]
    public async Task Adds_the_three_columns_and_backfills_existing_rows_to_the_dotnet_triple()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();

        await RemoveProfileColumnsAsync(connection);
        await InsertPreProfileWorkspaceAsync(connection, "AVR");

        Assert.Equal(0, await ProfileColumnCountAsync(connection));

        await Migrations.MigrateWorkspaceProfileColumnsAsync(db, NullLogger.Instance);

        Assert.Equal(3, await ProfileColumnCountAsync(connection));

        var workspace = await db.Workspaces.AsNoTracking().SingleAsync();
        Assert.Equal(WorkspaceType.DotNetDependency, workspace.Type);
        Assert.Equal(WorkspaceVersioningMode.GitVersion, workspace.VersioningMode);
        Assert.Equal(WorkspaceCiProvider.GitHubActions, workspace.CiProvider);
    }

    [Fact]
    public async Task Running_the_migration_twice_is_safe_and_the_second_run_changes_nothing()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();

        await RemoveProfileColumnsAsync(connection);
        await InsertPreProfileWorkspaceAsync(connection, "AVR");

        await Migrations.MigrateWorkspaceProfileColumnsAsync(db, NullLogger.Instance);
        await Migrations.MigrateWorkspaceProfileColumnsAsync(db, NullLogger.Instance);

        Assert.Equal(3, await ProfileColumnCountAsync(connection));

        var workspace = await db.Workspaces.AsNoTracking().SingleAsync();
        Assert.Equal(WorkspaceType.DotNetDependency, workspace.Type);
        Assert.Equal(WorkspaceVersioningMode.GitVersion, workspace.VersioningMode);
        Assert.Equal(WorkspaceCiProvider.GitHubActions, workspace.CiProvider);
    }

    [Fact]
    public async Task A_workspace_the_user_switched_to_Basic_is_never_overwritten_by_a_later_run()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();

        await RemoveProfileColumnsAsync(connection);
        await InsertPreProfileWorkspaceAsync(connection, "AVR");
        await InsertPreProfileWorkspaceAsync(connection, "Docs");

        await Migrations.MigrateWorkspaceProfileColumnsAsync(db, NullLogger.Instance);

        // The user then deliberately switches one workspace to Basic. The columns already exist from here on,
        // so the backfill must not run again and must not stomp that choice.
        var basic = await db.Workspaces.SingleAsync(workspace => workspace.Name == "Docs");
        basic.Type = WorkspaceType.Basic;
        basic.VersioningMode = WorkspaceVersioningMode.None;
        basic.CiProvider = WorkspaceCiProvider.None;
        await db.SaveChangesAsync();

        await Migrations.MigrateWorkspaceProfileColumnsAsync(db, NullLogger.Instance);

        db.ChangeTracker.Clear();

        var reloadedBasic = await db.Workspaces.AsNoTracking().SingleAsync(workspace => workspace.Name == "Docs");
        Assert.Equal(WorkspaceType.Basic, reloadedBasic.Type);
        Assert.Equal(WorkspaceVersioningMode.None, reloadedBasic.VersioningMode);
        Assert.Equal(WorkspaceCiProvider.None, reloadedBasic.CiProvider);

        var untouched = await db.Workspaces.AsNoTracking().SingleAsync(workspace => workspace.Name == "AVR");
        Assert.Equal(WorkspaceType.DotNetDependency, untouched.Type);
        Assert.Equal(WorkspaceVersioningMode.GitVersion, untouched.VersioningMode);
        Assert.Equal(WorkspaceCiProvider.GitHubActions, untouched.CiProvider);
    }

    [Fact]
    public async Task A_fresh_database_already_has_the_columns_and_keeps_the_model_defaults()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();

        Assert.Equal(3, await ProfileColumnCountAsync(connection));

        db.Workspaces.Add(new Workspace { Name = "fresh" });
        await db.SaveChangesAsync();

        await Migrations.MigrateWorkspaceProfileColumnsAsync(db, NullLogger.Instance);

        db.ChangeTracker.Clear();

        var workspace = await db.Workspaces.AsNoTracking().SingleAsync();
        Assert.Equal(WorkspaceType.Basic, workspace.Type);
        Assert.Equal(WorkspaceVersioningMode.None, workspace.VersioningMode);
        Assert.Equal(WorkspaceCiProvider.None, workspace.CiProvider);
    }

    [Fact]
    public async Task Runs_inside_the_strict_step_transaction()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();

        await RemoveProfileColumnsAsync(connection);
        await InsertPreProfileWorkspaceAsync(connection, "AVR");

        await Migrations.RunStrictStepAsync(
            db,
            4,
            "Workspace profile columns",
            dbContext => Migrations.MigrateWorkspaceProfileColumnsAsync(dbContext),
            backupPath: null,
            NullLogger.Instance);

        var workspace = await db.Workspaces.AsNoTracking().SingleAsync();
        Assert.Equal(WorkspaceType.DotNetDependency, workspace.Type);
        Assert.Equal(WorkspaceVersioningMode.GitVersion, workspace.VersioningMode);
        Assert.Equal(WorkspaceCiProvider.GitHubActions, workspace.CiProvider);
    }

    /// <summary>
    /// EnsureCreated() builds the Workspaces table from the current model, so the profile columns are already
    /// there. Dropping them reproduces the shape of a database created by a pre-profile build, which is the
    /// only population the migration exists for.
    /// </summary>
    private static async Task RemoveProfileColumnsAsync(SqliteConnection connection)
    {
        foreach (var columnName in new[] { "Type", "VersioningMode", "CiProvider" })
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = $"ALTER TABLE \"Workspaces\" DROP COLUMN \"{columnName}\"";
            await cmd.ExecuteNonQueryAsync();
        }
    }

    private static async Task InsertPreProfileWorkspaceAsync(SqliteConnection connection, string name)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "INSERT INTO \"Workspaces\" (\"Name\", \"IsDefault\", \"IsInSync\", \"ExcludeAiWorkflows\") VALUES ($name, 0, 0, 1)";
        var nameParameter = cmd.CreateParameter();
        nameParameter.ParameterName = "$name";
        nameParameter.Value = name;
        cmd.Parameters.Add(nameParameter);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<int> ProfileColumnCountAsync(SqliteConnection connection)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('Workspaces') WHERE name IN ('Type', 'VersioningMode', 'CiProvider')";
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }
}
