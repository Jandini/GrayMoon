using GrayMoon.Abstractions.Agent;
using GrayMoon.App.Data;
using GrayMoon.App.Models;
using GrayMoon.App.Repositories;
using GrayMoon.App.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GrayMoon.App.Tests;

public sealed class WorkspaceRepositoryExcludeAiWorkflowsTests
{
    private sealed class NoOpAgentBridge : IAgentBridge
    {
        public bool IsAgentConnected => false;

        public Task<AgentCommandResponse> SendCommandAsync(string command, object args, CancellationToken cancellationToken = default) =>
            Task.FromResult(new AgentCommandResponse(true, null, null));
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;

        public GitChangesTestDbContext.TestDbContextFactory Factory { get; }
        public AppDbContext CircuitDb { get; }
        public int WorkspaceId { get; }

        private Fixture(
            SqliteConnection connection,
            GitChangesTestDbContext.TestDbContextFactory factory,
            AppDbContext circuitDb,
            int workspaceId)
        {
            _connection = connection;
            Factory = factory;
            CircuitDb = circuitDb;
            WorkspaceId = workspaceId;
        }

        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
            var factory = new GitChangesTestDbContext.TestDbContextFactory(options);
            var db = factory.CreateDbContext();
            await db.Database.EnsureCreatedAsync();

            var workspace = new Workspace { Name = "test-workspace" };
            db.Workspaces.Add(workspace);
            await db.SaveChangesAsync();

            return new Fixture(connection, factory, db, workspace.WorkspaceId);
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

    [Fact]
    public async Task NewWorkspace_DefaultsToExcludeAiWorkflowsTrue()
    {
        await using var fx = await Fixture.CreateAsync();

        await using var db = fx.Factory.CreateDbContext();
        var workspace = await db.Workspaces.AsNoTracking().FirstAsync(w => w.WorkspaceId == fx.WorkspaceId);

        Assert.True(workspace.ExcludeAiWorkflows);
    }

    [Fact]
    public async Task UpdateExcludeAiWorkflowsAsync_PersistsFalseThenTrue()
    {
        await using var fx = await Fixture.CreateAsync();
        var repository = fx.CreateWorkspaceRepository();

        await repository.UpdateExcludeAiWorkflowsAsync(fx.WorkspaceId, false);
        await using (var afterDisable = fx.Factory.CreateDbContext())
        {
            var workspace = await afterDisable.Workspaces.AsNoTracking().FirstAsync(w => w.WorkspaceId == fx.WorkspaceId);
            Assert.False(workspace.ExcludeAiWorkflows);
        }

        await repository.UpdateExcludeAiWorkflowsAsync(fx.WorkspaceId, true);
        await using (var afterEnable = fx.Factory.CreateDbContext())
        {
            var workspace = await afterEnable.Workspaces.AsNoTracking().FirstAsync(w => w.WorkspaceId == fx.WorkspaceId);
            Assert.True(workspace.ExcludeAiWorkflows);
        }
    }
}
