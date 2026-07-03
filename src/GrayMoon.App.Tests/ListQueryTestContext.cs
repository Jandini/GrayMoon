using GrayMoon.App.Data;
using GrayMoon.App.Models;
using GrayMoon.App.Services.Queries;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace GrayMoon.App.Tests;

public sealed class ListQueryTestContext : IAsyncDisposable
{
    private readonly SqliteConnection _connection;

    public AppDbContext DbContext { get; }
    public RepositoryListQueryService RepositoryQuery { get; }
    public WorkspaceProjectListQueryService ProjectQuery { get; }

    private ListQueryTestContext(SqliteConnection connection, AppDbContext dbContext)
    {
        _connection = connection;
        DbContext = dbContext;
        RepositoryQuery = new RepositoryListQueryService(dbContext);
        ProjectQuery = new WorkspaceProjectListQueryService(dbContext);
    }

    public static async Task<ListQueryTestContext> CreateAsync(int repositoryCount = 120)
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;

        var dbContext = new AppDbContext(options);
        await dbContext.Database.EnsureCreatedAsync();

        var connector = new Connector
        {
            ConnectorName = "github-prod",
            ConnectorType = ConnectorType.GitHub,
            ApiBaseUrl = "https://api.github.com",
            IsActive = true,
        };
        dbContext.Connectors.Add(connector);
        await dbContext.SaveChangesAsync();

        for (var i = 1; i <= repositoryCount; i++)
        {
            var suffix = i.ToString("D4");
            dbContext.Repositories.Add(new Repository
            {
                ConnectorId = connector.ConnectorId,
                RepositoryName = i % 2 == 0 ? "graymoon-api" : $"repo-{suffix}",
                OrgName = i % 3 == 0 ? "acme" : "other",
                Visibility = "Public",
                CloneUrl = $"https://github.com/acme/repo-{suffix}.git",
                Topics = i % 5 == 0 ? "blazor,angular" : null,
            });
        }

        await dbContext.SaveChangesAsync();

        var workspace = new Workspace { Name = "test-ws" };
        dbContext.Workspaces.Add(workspace);
        await dbContext.SaveChangesAsync();

        var repos = await dbContext.Repositories.OrderBy(r => r.RepositoryName).ThenBy(r => r.RepositoryId).Take(10).ToListAsync();
        foreach (var repo in repos)
        {
            dbContext.WorkspaceProjects.Add(new WorkspaceProject
            {
                WorkspaceId = workspace.WorkspaceId,
                RepositoryId = repo.RepositoryId,
                ProjectName = $"Project-{repo.RepositoryName}",
                ProjectType = ProjectType.Service,
                ProjectFilePath = $"src/{repo.RepositoryName}/{repo.RepositoryName}.csproj",
                TargetFramework = "net10.0",
            });
        }

        await dbContext.SaveChangesAsync();
        return new ListQueryTestContext(connection, dbContext);
    }

    public async ValueTask DisposeAsync()
    {
        await DbContext.DisposeAsync();
        await _connection.DisposeAsync();
    }
}
