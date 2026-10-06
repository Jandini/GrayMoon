using GrayMoon.App.Data;
using GrayMoon.App.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace GrayMoon.App.Tests;

public sealed class WorkspaceRepositoryRoleMigrationTests
{
    private const string IndexName = "IX_WorkspaceRepositories_WorkspaceId_WorkspaceRole";

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
    public async Task Existing_links_get_Role_Source_after_migration()
    {
        var (connection, db) = await CreateDbAsync();
        await using var _ = connection;
        await using var __ = db;

        await SeedLinksAsync(db, workspaceCount: 1, linksPerWorkspace: 2);
        await RemoveRoleColumnAsync(connection);

        Assert.Equal(0, await RoleColumnCountAsync(connection));

        await Migrations.MigrateWorkspaceRepositoryRoleAsync(db, NullLogger.Instance);

        Assert.Equal(1, await RoleColumnCountAsync(connection));

        db.ChangeTracker.Clear();
        var links = await db.WorkspaceRepositories.AsNoTracking().ToListAsync();
        Assert.Equal(2, links.Count);
        Assert.All(links, link => Assert.Equal(WorkspaceRepositoryRole.Source, link.Role));
    }

    [Fact]
    public async Task Migration_is_idempotent()
    {
        var (connection, db) = await CreateDbAsync();
        await using var _ = connection;
        await using var __ = db;

        await SeedLinksAsync(db, workspaceCount: 1, linksPerWorkspace: 1);
        await RemoveRoleColumnAsync(connection);

        await Migrations.MigrateWorkspaceRepositoryRoleAsync(db, NullLogger.Instance);
        await Migrations.MigrateWorkspaceRepositoryRoleAsync(db, NullLogger.Instance);

        Assert.Equal(1, await RoleColumnCountAsync(connection));
        Assert.Equal(1, await IndexCountAsync(connection));

        db.ChangeTracker.Clear();
        var link = await db.WorkspaceRepositories.AsNoTracking().SingleAsync();
        Assert.Equal(WorkspaceRepositoryRole.Source, link.Role);
    }

    [Fact]
    public async Task Second_Workspace_role_link_in_same_workspace_is_rejected_by_index()
    {
        var (connection, db) = await CreateDbAsync();
        await using var _ = connection;
        await using var __ = db;

        var links = await SeedLinksAsync(db, workspaceCount: 1, linksPerWorkspace: 2);

        links[0].Role = WorkspaceRepositoryRole.Workspace;
        await db.SaveChangesAsync();

        links[1].Role = WorkspaceRepositoryRole.Workspace;
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Workspace_role_links_in_different_workspaces_coexist()
    {
        var (connection, db) = await CreateDbAsync();
        await using var _ = connection;
        await using var __ = db;

        var links = await SeedLinksAsync(db, workspaceCount: 2, linksPerWorkspace: 1);

        foreach (var link in links)
            link.Role = WorkspaceRepositoryRole.Workspace;
        await db.SaveChangesAsync();

        db.ChangeTracker.Clear();
        var reloaded = await db.WorkspaceRepositories.AsNoTracking().ToListAsync();
        Assert.Equal(2, reloaded.Count);
        Assert.All(reloaded, link => Assert.Equal(WorkspaceRepositoryRole.Workspace, link.Role));
    }

    [Fact]
    public async Task Fresh_database_has_filtered_index()
    {
        var (connection, db) = await CreateDbAsync();
        await using var _ = connection;
        await using var __ = db;

        Assert.Equal(1, await IndexCountAsync(connection));
    }

    private static async Task<List<WorkspaceRepositoryLink>> SeedLinksAsync(AppDbContext db, int workspaceCount, int linksPerWorkspace)
    {
        var connector = new Connector
        {
            ConnectorName = "github-test",
            ConnectorType = ConnectorType.GitHub,
            ApiBaseUrl = "https://api.github.com",
            IsActive = true,
            IsHealthy = true,
        };
        db.Connectors.Add(connector);
        await db.SaveChangesAsync();

        var links = new List<WorkspaceRepositoryLink>();
        for (var w = 0; w < workspaceCount; w++)
        {
            var workspace = new Workspace { Name = $"ws-{w}" };
            db.Workspaces.Add(workspace);
            await db.SaveChangesAsync();

            for (var r = 0; r < linksPerWorkspace; r++)
            {
                var repository = new Repository
                {
                    ConnectorId = connector.ConnectorId,
                    RepositoryName = $"repo-{w}-{r}",
                    CloneUrl = $"https://example.invalid/repo-{w}-{r}.git",
                };
                db.Repositories.Add(repository);
                await db.SaveChangesAsync();

                var link = new WorkspaceRepositoryLink
                {
                    WorkspaceId = workspace.WorkspaceId,
                    RepositoryId = repository.RepositoryId,
                };
                db.WorkspaceRepositories.Add(link);
                await db.SaveChangesAsync();
                links.Add(link);
            }
        }

        return links;
    }

    /// <summary>
    /// EnsureCreated() builds the table from the current model, so Role and its index are already there.
    /// Dropping the index and then the column reproduces the shape of a database created by an earlier build.
    /// </summary>
    private static async Task RemoveRoleColumnAsync(SqliteConnection connection)
    {
        await using (var dropIndex = connection.CreateCommand())
        {
            dropIndex.CommandText = $"DROP INDEX IF EXISTS \"{IndexName}\"";
            await dropIndex.ExecuteNonQueryAsync();
        }

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "ALTER TABLE \"WorkspaceRepositories\" DROP COLUMN \"Role\"";
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<int> RoleColumnCountAsync(SqliteConnection connection)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('WorkspaceRepositories') WHERE name = 'Role'";
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    private static async Task<int> IndexCountAsync(SqliteConnection connection)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = '{IndexName}'";
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }
}