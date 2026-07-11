using GrayMoon.App.Data;
using GrayMoon.App.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace GrayMoon.App.Tests;

public class MigrationsTests
{
    [Fact]
    public async Task MigrateWorkspaceGitChangesAsync_creates_new_tables_on_an_already_provisioned_database_without_touching_existing_data()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;

        // Simulate an existing, already-provisioned database: EnsureCreated() builds the full current
        // schema (including the new Git Changes tables), then we drop those two tables to reproduce
        // what a pre-existing database looks like before this feature shipped. EnsureCreated() alone
        // only creates a schema when the database has zero tables, so this is the realistic scenario
        // the migration method must actually handle.
        await using (var seedContext = new AppDbContext(options))
        {
            await seedContext.Database.EnsureCreatedAsync();

            seedContext.Connectors.Add(new Connector
            {
                ConnectorName = "github-prod",
                ConnectorType = ConnectorType.GitHub,
                ApiBaseUrl = "https://api.github.com",
                IsActive = true,
            });
            await seedContext.SaveChangesAsync();

            await seedContext.Database.ExecuteSqlRawAsync("DROP TABLE WorkspaceGitChangeEntries");
            await seedContext.Database.ExecuteSqlRawAsync("DROP TABLE WorkspaceGitRepositoryStatus");
        }

        await using var dbContext = new AppDbContext(options);

        var tableCountBefore = await CountTablesAsync(dbContext, "WorkspaceGitRepositoryStatus", "WorkspaceGitChangeEntries");
        Assert.Equal(0, tableCountBefore);

        await Migrations.MigrateWorkspaceGitChangesAsync(dbContext);

        var tableCountAfter = await CountTablesAsync(dbContext, "WorkspaceGitRepositoryStatus", "WorkspaceGitChangeEntries");
        Assert.Equal(2, tableCountAfter);

        // Existing, unrelated data must survive untouched.
        var connector = Assert.Single(dbContext.Connectors);
        Assert.Equal("github-prod", connector.ConnectorName);

        // The tables must actually be usable, not just present - insert through a real FK-satisfying row.
        var workspace = new Workspace { Name = "verify-workspace" };
        dbContext.Workspaces.Add(workspace);
        await dbContext.SaveChangesAsync();

        var repository = new Repository
        {
            ConnectorId = connector.ConnectorId,
            RepositoryName = "verify-repo",
            Visibility = "Public",
            CloneUrl = "https://github.com/acme/verify-repo.git",
        };
        dbContext.Repositories.Add(repository);
        await dbContext.SaveChangesAsync();

        var link = new WorkspaceRepositoryLink { WorkspaceId = workspace.WorkspaceId, RepositoryId = repository.RepositoryId };
        dbContext.WorkspaceRepositories.Add(link);
        await dbContext.SaveChangesAsync();

        dbContext.WorkspaceGitRepositoryStatuses.Add(new WorkspaceGitRepositoryStatus
        {
            WorkspaceRepositoryId = link.WorkspaceRepositoryId,
            SnapshotVersion = 1,
            AgentScannedAt = DateTimeOffset.UtcNow,
            PersistedAt = DateTimeOffset.UtcNow,
        });
        var saveEx = await Record.ExceptionAsync(() => dbContext.SaveChangesAsync());
        Assert.Null(saveEx);
    }

    [Fact]
    public async Task MigrateWorkspaceGitChangesAsync_is_idempotent_when_tables_already_exist()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;

        await using var dbContext = new AppDbContext(options);
        await dbContext.Database.EnsureCreatedAsync();

        // Running the migration again against a database that already has the tables must not throw
        // and must not duplicate/alter them.
        var ex = await Record.ExceptionAsync(() => Migrations.MigrateWorkspaceGitChangesAsync(dbContext));
        Assert.Null(ex);

        var tableCount = await CountTablesAsync(dbContext, "WorkspaceGitRepositoryStatus", "WorkspaceGitChangeEntries");
        Assert.Equal(2, tableCount);
    }

    private static async Task<int> CountTablesAsync(AppDbContext dbContext, params string[] tableNames)
    {
        var conn = dbContext.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
        {
            await conn.OpenAsync();
        }

        var count = 0;
        foreach (var name in tableNames)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=$name";
            var param = cmd.CreateParameter();
            param.ParameterName = "$name";
            param.Value = name;
            cmd.Parameters.Add(param);
            count += Convert.ToInt32(await cmd.ExecuteScalarAsync());
        }

        return count;
    }
}
