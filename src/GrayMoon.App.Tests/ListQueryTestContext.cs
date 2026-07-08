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
    public WorkspaceRepositoryLinkListQueryService WorkspaceRepoLinkQuery { get; }

    private ListQueryTestContext(SqliteConnection connection, AppDbContext dbContext, DbContextOptions<AppDbContext> options)
    {
        _connection = connection;
        DbContext = dbContext;
        RepositoryQuery = new RepositoryListQueryService(dbContext);
        ProjectQuery = new WorkspaceProjectListQueryService(dbContext);
        WorkspaceRepoLinkQuery = new WorkspaceRepositoryLinkListQueryService(new TestDbContextFactory(options));
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

        var allRepos = await dbContext.Repositories.OrderBy(r => r.RepositoryId).ToListAsync();
        for (var i = 0; i < allRepos.Count; i++)
        {
            var repo = allRepos[i];
            dbContext.WorkspaceRepositories.Add(new WorkspaceRepositoryLink
            {
                WorkspaceId = workspace.WorkspaceId,
                RepositoryId = repo.RepositoryId,
                GitVersion = $"1.0.{i}",
                BranchName = i % 4 == 0 ? "main" : $"feature-{i % 10}",
                DefaultBranchName = "main",
                DependencyLevel = i % 5,
                Dependencies = i % 7,
                UnmatchedDeps = i % 11 == 0 ? 1 : 0,
                OutOfDateFileRepos = i % 13 == 0 ? 1 : 0,
                OutgoingCommits = i % 3 == 0 ? 2 : 0,
                IncomingCommits = i % 6 == 0 ? 1 : 0,
                RepositoryType = i % 3 == 0 ? ProjectType.Service : ProjectType.Library,
                SyncStatus = i % 8 == 0 ? RepoSyncStatus.NeedsSync : RepoSyncStatus.InSync,
            });
        }

        await dbContext.SaveChangesAsync();
        return new ListQueryTestContext(connection, dbContext, options);
    }

    public static async Task<(ListQueryTestContext Context, int WorkspaceId)> CreateWithWorkspaceLinksAsync(int repositoryCount = 120)
    {
        var ctx = await CreateAsync(repositoryCount);
        var workspaceId = ctx.DbContext.Workspaces.Select(w => w.WorkspaceId).First();
        return (ctx, workspaceId);
    }

    public async ValueTask DisposeAsync()
    {
        await DbContext.DisposeAsync();
        await _connection.DisposeAsync();
    }

    private sealed class TestDbContextFactory(DbContextOptions<AppDbContext> options) : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(options);
    }
}
