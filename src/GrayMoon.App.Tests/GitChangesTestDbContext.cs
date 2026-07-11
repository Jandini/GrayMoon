using GrayMoon.App.Data;
using GrayMoon.App.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace GrayMoon.App.Tests;

/// <summary>Lightweight in-memory SQLite fixture for Git Changes persistence tests - one connector,
/// repository, workspace, and workspace-repository link, seeded fresh per test.</summary>
public sealed class GitChangesTestDbContext : IAsyncDisposable
{
    private readonly SqliteConnection _connection;
    public DbContextOptions<AppDbContext> Options { get; }
    public AppDbContext DbContext { get; }
    public int WorkspaceId { get; private set; }
    public int RepositoryId { get; private set; }
    public int WorkspaceRepositoryId { get; private set; }

    private GitChangesTestDbContext(SqliteConnection connection, DbContextOptions<AppDbContext> options, AppDbContext dbContext)
    {
        _connection = connection;
        Options = options;
        DbContext = dbContext;
    }

    public static async Task<GitChangesTestDbContext> CreateAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        var dbContext = new AppDbContext(options);
        await dbContext.Database.EnsureCreatedAsync();

        var ctx = new GitChangesTestDbContext(connection, options, dbContext);
        await ctx.SeedAsync();
        return ctx;
    }

    private async Task SeedAsync()
    {
        var connector = new Connector
        {
            ConnectorName = "github-prod",
            ConnectorType = ConnectorType.GitHub,
            ApiBaseUrl = "https://api.github.com",
            IsActive = true,
        };
        DbContext.Connectors.Add(connector);
        await DbContext.SaveChangesAsync();

        var repository = new Repository
        {
            ConnectorId = connector.ConnectorId,
            RepositoryName = "graymoon-api",
            OrgName = "acme",
            Visibility = "Public",
            CloneUrl = "https://github.com/acme/graymoon-api.git",
        };
        DbContext.Repositories.Add(repository);
        await DbContext.SaveChangesAsync();

        var workspace = new Workspace { Name = "test-workspace" };
        DbContext.Workspaces.Add(workspace);
        await DbContext.SaveChangesAsync();

        var link = new WorkspaceRepositoryLink
        {
            WorkspaceId = workspace.WorkspaceId,
            RepositoryId = repository.RepositoryId,
        };
        DbContext.WorkspaceRepositories.Add(link);
        await DbContext.SaveChangesAsync();

        WorkspaceId = workspace.WorkspaceId;
        RepositoryId = repository.RepositoryId;
        WorkspaceRepositoryId = link.WorkspaceRepositoryId;
    }

    public async ValueTask DisposeAsync()
    {
        await DbContext.DisposeAsync();
        await _connection.DisposeAsync();
    }

    public sealed class TestDbContextFactory(DbContextOptions<AppDbContext> options) : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(options);
    }
}
