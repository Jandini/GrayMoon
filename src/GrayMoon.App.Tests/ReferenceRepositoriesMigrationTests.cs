using GrayMoon.App.Data;
using GrayMoon.App.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace GrayMoon.App.Tests;

public sealed class ReferenceRepositoriesMigrationTests
{
    private static readonly (string Table, string Column)[] Columns =
    [
        ("Workspaces", "ReferenceDefault"),
        ("WorkspaceRepositories", "IsReference"),
        ("WorkspaceFeatureRepositories", "IsReferenceSnapshot"),
        ("WorkspaceFeatureRepositories", "SourceBranchName"),
    ];

    private static async Task<(SqliteConnection Connection, AppDbContext Db)> CreateDbAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        var db = new AppDbContext(options);
        await db.Database.CreateSchemaAsync();
        return (connection, db);
    }

    [Fact]
    public async Task Old_database_gets_the_columns_and_every_existing_row_is_writable()
    {
        var (connection, db) = await CreateDbAsync();
        await using var _ = connection;
        await using var __ = db;

        await SeedLinkAsync(db, WorkspaceRepositoryRole.Source, isReference: false);
        await DropColumnsAsync(connection);
        Assert.All(await ColumnCountsAsync(connection), count => Assert.Equal(0, count));

        await Migrations.MigrateReferenceRepositoriesAsync(db, NullLogger.Instance);
        await Migrations.MigrateReferenceRepositoriesAsync(db, NullLogger.Instance);

        Assert.All(await ColumnCountsAsync(connection), count => Assert.Equal(1, count));
        db.ChangeTracker.Clear();
        Assert.False((await db.Workspaces.AsNoTracking().SingleAsync()).ReferenceDefault);
        Assert.False((await db.WorkspaceRepositories.AsNoTracking().SingleAsync()).IsReference);
    }

    [Fact]
    public async Task Fresh_database_keeps_its_columns_and_stores_the_values()
    {
        var (connection, db) = await CreateDbAsync();
        await using var _ = connection;
        await using var __ = db;

        await Migrations.MigrateReferenceRepositoriesAsync(db, NullLogger.Instance);

        Assert.All(await ColumnCountsAsync(connection), count => Assert.Equal(1, count));
        await SeedLinkAsync(db, WorkspaceRepositoryRole.Source, isReference: true, referenceDefault: true);
        db.ChangeTracker.Clear();

        Assert.True((await db.Workspaces.AsNoTracking().SingleAsync()).ReferenceDefault);
        Assert.True((await db.WorkspaceRepositories.AsNoTracking().SingleAsync()).IsReference);
    }

    [Fact]
    public async Task Strict_step_19_is_registered_after_step_18()
    {
        var (connection, db) = await CreateDbAsync();
        await using var _ = connection;
        await using var __ = db;

        var step = Migrations.StrictSteps.Single(s => s.Version == 19);
        Assert.Contains(Migrations.StrictSteps, s => s.Version == 18);

        await DropColumnsAsync(connection);
        await step.Action(db);

        Assert.All(await ColumnCountsAsync(connection), count => Assert.Equal(1, count));
    }

    [Fact]
    public async Task The_Workspace_repository_is_never_saved_as_a_reference_repository()
    {
        var (connection, db) = await CreateDbAsync();
        await using var _ = connection;
        await using var __ = db;

        await SeedLinkAsync(db, WorkspaceRepositoryRole.Workspace, isReference: true);
        db.ChangeTracker.Clear();
        Assert.False((await db.WorkspaceRepositories.AsNoTracking().SingleAsync()).IsReference);

        var tracked = await db.WorkspaceRepositories.SingleAsync();
        tracked.IsReference = true;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        Assert.False((await db.WorkspaceRepositories.AsNoTracking().SingleAsync()).IsReference);
    }

    [Fact]
    public void A_link_copy_with_another_branch_keeps_the_reference_fields()
    {
        var link = new WorkspaceRepositoryLink
        {
            BranchName = "main",
            IsReference = true,
            IsReferenceSnapshot = true,
            FeatureSourceBranchName = "release/1",
        };

        var copy = link.WithBranchOverride("other");

        Assert.Equal("other", copy.BranchName);
        Assert.True(copy.IsReference);
        Assert.True(copy.IsReferenceSnapshot);
        Assert.Equal("release/1", copy.FeatureSourceBranchName);
    }

    private static async Task SeedLinkAsync(AppDbContext db, WorkspaceRepositoryRole role, bool isReference, bool referenceDefault = false)
    {
        var connector = new Connector
        {
            ConnectorName = "github-test",
            ConnectorType = ConnectorType.GitHub,
            ApiBaseUrl = "https://api.github.com",
            IsActive = true,
            IsHealthy = true,
        };
        var workspace = new Workspace { Name = "ws", ReferenceDefault = referenceDefault };
        db.Connectors.Add(connector);
        db.Workspaces.Add(workspace);
        await db.SaveChangesAsync();
        var repository = new Repository { ConnectorId = connector.ConnectorId, RepositoryName = "repo", CloneUrl = "https://example.invalid/repo.git" };
        db.Repositories.Add(repository);
        await db.SaveChangesAsync();
        db.WorkspaceRepositories.Add(new WorkspaceRepositoryLink
        {
            WorkspaceId = workspace.WorkspaceId,
            RepositoryId = repository.RepositoryId,
            Role = role,
            IsReference = isReference,
        });
        await db.SaveChangesAsync();
    }

    private static async Task DropColumnsAsync(SqliteConnection connection)
    {
        foreach (var (table, column) in Columns)
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = $"ALTER TABLE \"{table}\" DROP COLUMN \"{column}\"";
            await cmd.ExecuteNonQueryAsync();
        }
    }

    private static async Task<List<int>> ColumnCountsAsync(SqliteConnection connection)
    {
        var counts = new List<int>();
        foreach (var (table, column) in Columns)
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = '{column}'";
            counts.Add(Convert.ToInt32(await cmd.ExecuteScalarAsync()));
        }

        return counts;
    }
}
