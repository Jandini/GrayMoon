using GrayMoon.Abstractions.Agent;
using GrayMoon.App.Data;
using GrayMoon.App.Models;
using GrayMoon.App.Repositories;
using GrayMoon.App.Services;
using GrayMoon.Common.Git;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GrayMoon.App.Tests;

public sealed class WorkspaceRepositoryReplaceTests
{
    private sealed class NoOpAgentBridge : IAgentBridge
    {
        public bool IsAgentConnected => false;

        public Task<AgentCommandResponse> SendCommandAsync(string command, object args, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Not used by these tests.");
    }

    [Fact]
    public async Task UpdateAsync_succeeds_when_circuit_context_tracks_wrl_and_workspace()
    {
        await using var fx = await Fixture.CreateAsync(linkCount: 2);

        _ = await fx.CircuitDb.WorkspaceRepositories
            .Include(l => l.Workspace)
            .Include(l => l.Repository)
            .FirstAsync(l => l.WorkspaceRepositoryId == fx.LinkIds[0]);

        var catalog = fx.CreateWorkspaceRepository();
        await catalog.UpdateAsync(fx.WorkspaceId, "test-workspace", [fx.RepositoryIds[1]], null);

        await using var afterRemove = fx.Factory.CreateDbContext();
        var remaining = await afterRemove.WorkspaceRepositories
            .AsNoTracking()
            .Where(wr => wr.WorkspaceId == fx.WorkspaceId)
            .ToListAsync();
        Assert.Equal(fx.RepositoryIds[1], Assert.Single(remaining).RepositoryId);

        await catalog.UpdateAsync(fx.WorkspaceId, "test-workspace", fx.RepositoryIds, null);

        await using var afterReadd = fx.Factory.CreateDbContext();
        var relinked = await afterReadd.WorkspaceRepositories
            .AsNoTracking()
            .Where(wr => wr.WorkspaceId == fx.WorkspaceId)
            .Select(wr => wr.RepositoryId)
            .OrderBy(id => id)
            .ToListAsync();
        Assert.Equal(fx.RepositoryIds.OrderBy(id => id), relinked);
    }

    [Fact]
    public async Task UpdateAsync_unlinks_repo_that_has_project_dependencies_and_git_changes()
    {
        await using var fx = await Fixture.CreateAsync(linkCount: 2);

        var projectA = new WorkspaceProject
        {
            WorkspaceId = fx.WorkspaceId,
            RepositoryId = fx.RepositoryIds[0],
            ProjectName = "LibA",
            ProjectType = ProjectType.Library,
            ProjectFilePath = "src/LibA/LibA.csproj",
            TargetFramework = "net10.0",
        };
        var projectB = new WorkspaceProject
        {
            WorkspaceId = fx.WorkspaceId,
            RepositoryId = fx.RepositoryIds[1],
            ProjectName = "LibB",
            ProjectType = ProjectType.Library,
            ProjectFilePath = "src/LibB/LibB.csproj",
            TargetFramework = "net10.0",
        };
        fx.CircuitDb.WorkspaceProjects.AddRange(projectA, projectB);
        await fx.CircuitDb.SaveChangesAsync();

        fx.CircuitDb.ProjectDependencies.Add(new ProjectDependency
        {
            DependentProjectId = projectA.ProjectId,
            ReferencedProjectId = projectB.ProjectId,
            Version = "1.0.0",
        });
        fx.CircuitDb.WorkspaceRepositoryCustomDependencies.Add(new WorkspaceRepositoryCustomDependency
        {
            DependentWorkspaceRepositoryId = fx.LinkIds[0],
            ReferencedWorkspaceRepositoryId = fx.LinkIds[1],
        });
        fx.CircuitDb.WorkspaceGitRepositoryStatuses.Add(new WorkspaceGitRepositoryStatus
        {
            WorkspaceRepositoryId = fx.LinkIds[0],
            SnapshotVersion = 1,
            BranchName = "main",
            AgentScannedAt = DateTimeOffset.UtcNow,
            PersistedAt = DateTimeOffset.UtcNow,
        });
        fx.CircuitDb.WorkspaceGitChangeEntries.Add(new WorkspaceGitChangeEntry
        {
            WorkspaceRepositoryId = fx.LinkIds[0],
            Path = "src/LibA/Class1.cs",
            IndexChange = GitChangeKind.None,
            WorktreeChange = GitChangeKind.Modified,
            IsTracked = true,
        });
        await fx.CircuitDb.SaveChangesAsync();

        var catalog = fx.CreateWorkspaceRepository();
        await catalog.UpdateAsync(fx.WorkspaceId, "test-workspace", [fx.RepositoryIds[1]], null);

        await using var verify = fx.Factory.CreateDbContext();
        Assert.False(await verify.WorkspaceRepositories.AnyAsync(wr => wr.WorkspaceRepositoryId == fx.LinkIds[0]));
        Assert.True(await verify.WorkspaceRepositories.AnyAsync(wr => wr.WorkspaceRepositoryId == fx.LinkIds[1]));
        Assert.False(await verify.WorkspaceProjects.AnyAsync(p => p.RepositoryId == fx.RepositoryIds[0]));
        Assert.True(await verify.WorkspaceProjects.AnyAsync(p => p.RepositoryId == fx.RepositoryIds[1]));
        Assert.Empty(await verify.ProjectDependencies.ToListAsync());
        Assert.Empty(await verify.WorkspaceRepositoryCustomDependencies.ToListAsync());
        Assert.Empty(await verify.WorkspaceGitRepositoryStatuses.ToListAsync());
        Assert.Empty(await verify.WorkspaceGitChangeEntries.ToListAsync());
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;

        public GitChangesTestDbContext.TestDbContextFactory Factory { get; }
        public AppDbContext CircuitDb { get; }
        public int WorkspaceId { get; }
        public IReadOnlyList<int> RepositoryIds { get; }
        public IReadOnlyList<int> LinkIds { get; }

        private Fixture(
            SqliteConnection connection,
            GitChangesTestDbContext.TestDbContextFactory factory,
            AppDbContext circuitDb,
            int workspaceId,
            IReadOnlyList<int> repositoryIds,
            IReadOnlyList<int> linkIds)
        {
            _connection = connection;
            Factory = factory;
            CircuitDb = circuitDb;
            WorkspaceId = workspaceId;
            RepositoryIds = repositoryIds;
            LinkIds = linkIds;
        }

        public static async Task<Fixture> CreateAsync(int linkCount)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
            var factory = new GitChangesTestDbContext.TestDbContextFactory(options);
            var db = factory.CreateDbContext();
            await db.Database.EnsureCreatedAsync();

            var connector = new Connector
            {
                ConnectorName = "github-prod",
                ConnectorType = ConnectorType.GitHub,
                ApiBaseUrl = "https://api.github.com",
                IsActive = true,
            };
            db.Connectors.Add(connector);
            await db.SaveChangesAsync();

            var repositoryIds = new List<int>();
            for (var i = 0; i < linkCount; i++)
            {
                var repository = new Repository
                {
                    ConnectorId = connector.ConnectorId,
                    RepositoryName = $"repo-{i}",
                    OrgName = "acme",
                    Visibility = "Public",
                    CloneUrl = $"https://github.com/acme/repo-{i}.git",
                };
                db.Repositories.Add(repository);
                await db.SaveChangesAsync();
                repositoryIds.Add(repository.RepositoryId);
            }

            var workspace = new Workspace { Name = "test-workspace" };
            db.Workspaces.Add(workspace);
            await db.SaveChangesAsync();

            var linkIds = new List<int>();
            foreach (var repositoryId in repositoryIds)
            {
                var link = new WorkspaceRepositoryLink
                {
                    WorkspaceId = workspace.WorkspaceId,
                    RepositoryId = repositoryId,
                };
                db.WorkspaceRepositories.Add(link);
                await db.SaveChangesAsync();
                linkIds.Add(link.WorkspaceRepositoryId);
            }

            return new Fixture(connection, factory, db, workspace.WorkspaceId, repositoryIds, linkIds);
        }

        public WorkspaceRepository CreateWorkspaceRepository()
        {
            var workspaceService = new WorkspaceService(
                new NoOpAgentBridge(),
                NullLogger<WorkspaceService>.Instance,
                new AppSettingRepository(CircuitDb),
                Options.Create(new WorkspaceOptions()));

            return new WorkspaceRepository(
                CircuitDb,
                Factory,
                workspaceService,
                NullLogger<WorkspaceRepository>.Instance);
        }

        public async ValueTask DisposeAsync()
        {
            await CircuitDb.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }
}
