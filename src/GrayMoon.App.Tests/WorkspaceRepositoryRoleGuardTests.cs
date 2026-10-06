using GrayMoon.Abstractions.Worker;
using GrayMoon.App.Data;
using GrayMoon.App.Models;
using GrayMoon.App.Repositories;
using GrayMoon.App.Services;
using GrayMoon.App.Services.GitChanges;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GrayMoon.App.Tests;

public sealed class WorkspaceRepositoryRoleGuardTests
{
    private sealed class NoOpWorkerBridge : IWorkerBridge
    {
        public bool IsWorkerConnected => false;

        public Task<WorkerCommandResponse> SendCommandAsync(string command, object args, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Not used by these tests.");
    }

    private static async Task<(SqliteConnection Connection, AppDbContext Db)> CreateDbAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();
        return (connection, db);
    }

    private static WorkspaceRepository CreateWorkspaceRepository(AppDbContext db)
    {
        var workspaceService = new WorkspaceService(
            new NoOpWorkerBridge(),
            NullLogger<WorkspaceService>.Instance,
            new AppSettingRepository(db),
            Options.Create(new WorkspaceOptions()));

        return new WorkspaceRepository(
            db,
            new GitChangesTestDbContext.TestDbContextFactory(
                new DbContextOptionsBuilder<AppDbContext>().UseSqlite(db.Database.GetDbConnection()).Options),
            workspaceService,
            new WorkspaceGitChangesNotifier(NullLogger<WorkspaceGitChangesNotifier>.Instance),
            NullLogger<WorkspaceRepository>.Instance);
    }

    private static async Task<(Workspace Workspace, int WorkspaceRepoId, int SourceRepoId, int OtherRepoId)> SeedAsync(AppDbContext db)
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

        var workspace = new Workspace { Name = "ws" };
        db.Workspaces.Add(workspace);
        await db.SaveChangesAsync();

        var repositories = new[] { "ws-repo", "source-repo", "other-repo" }
            .Select(name => new Repository
            {
                ConnectorId = connector.ConnectorId,
                RepositoryName = name,
                CloneUrl = $"https://example.invalid/{name}.git",
            })
            .ToList();
        db.Repositories.AddRange(repositories);
        await db.SaveChangesAsync();

        db.WorkspaceRepositories.Add(new WorkspaceRepositoryLink
        {
            WorkspaceId = workspace.WorkspaceId,
            RepositoryId = repositories[0].RepositoryId,
            Role = WorkspaceRepositoryRole.Workspace,
        });
        db.WorkspaceRepositories.Add(new WorkspaceRepositoryLink
        {
            WorkspaceId = workspace.WorkspaceId,
            RepositoryId = repositories[1].RepositoryId,
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        return (workspace, repositories[0].RepositoryId, repositories[1].RepositoryId, repositories[2].RepositoryId);
    }

    [Fact]
    public async Task Replace_membership_never_removes_workspace_role_link()
    {
        var (connection, db) = await CreateDbAsync();
        await using var _ = connection;
        await using var __ = db;

        var (workspace, workspaceRepoId, sourceRepoId, otherRepoId) = await SeedAsync(db);
        var repository = CreateWorkspaceRepository(db);

        // The requested set contains neither the Workspace-role repository nor the existing Source.
        await repository.UpdateAsync(workspace.WorkspaceId, workspace.Name, [otherRepoId], rootPath: null);

        db.ChangeTracker.Clear();
        var links = await db.WorkspaceRepositories.AsNoTracking()
            .Where(link => link.WorkspaceId == workspace.WorkspaceId)
            .ToListAsync();

        Assert.Contains(links, link => link.RepositoryId == workspaceRepoId && link.Role == WorkspaceRepositoryRole.Workspace);
        Assert.DoesNotContain(links, link => link.RepositoryId == sourceRepoId);
        Assert.Contains(links, link => link.RepositoryId == otherRepoId && link.Role == WorkspaceRepositoryRole.Source);
    }

    [Fact]
    public async Task Replace_membership_skips_repository_that_is_the_workspace_repository()
    {
        var (connection, db) = await CreateDbAsync();
        await using var _ = connection;
        await using var __ = db;

        var (workspace, workspaceRepoId, sourceRepoId, otherRepoId) = await SeedAsync(db);
        var repository = CreateWorkspaceRepository(db);

        await repository.UpdateAsync(workspace.WorkspaceId, workspace.Name, [workspaceRepoId, sourceRepoId, otherRepoId], rootPath: null);

        db.ChangeTracker.Clear();
        var links = await db.WorkspaceRepositories.AsNoTracking()
            .Where(link => link.WorkspaceId == workspace.WorkspaceId)
            .ToListAsync();

        Assert.Equal(3, links.Count);
        var workspaceRepoLinks = links.Where(link => link.RepositoryId == workspaceRepoId).ToList();
        Assert.Single(workspaceRepoLinks);
        Assert.Equal(WorkspaceRepositoryRole.Workspace, workspaceRepoLinks[0].Role);
        Assert.Contains(links, link => link.RepositoryId == otherRepoId && link.Role == WorkspaceRepositoryRole.Source);
    }
}