using GrayMoon.Abstractions.Agent;
using GrayMoon.App.Data;
using GrayMoon.App.Models;
using GrayMoon.App.Repositories;
using GrayMoon.App.Services;
using GrayMoon.App.Services.Application;
using GrayMoon.Application;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GrayMoon.App.Tests;

public sealed class WorkspaceFacadeOperationsTests
{
    private sealed class NoOpAgentBridge : IAgentBridge
    {
        public bool IsAgentConnected => false;

        public Task<AgentCommandResponse> SendCommandAsync(string command, object args, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Not used by these tests.");
    }

    private sealed class ListProgress : IProgress<OperationProgress>
    {
        public List<string> Messages { get; } = [];

        public void Report(OperationProgress value) => Messages.Add(value.Message);
    }

    private static async Task<(SqliteConnection Connection, AppDbContext DbContext)> CreateEmptyDbAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        var dbContext = new AppDbContext(options);
        await dbContext.Database.EnsureCreatedAsync();
        return (connection, dbContext);
    }

    private static WorkspaceRepository CreateWorkspaceRepository(AppDbContext dbContext)
    {
        var workspaceService = new WorkspaceService(
            new NoOpAgentBridge(),
            NullLogger<WorkspaceService>.Instance,
            new AppSettingRepository(dbContext),
            Options.Create(new WorkspaceOptions()));

        return new WorkspaceRepository(
            dbContext,
            new GitChangesTestDbContext.TestDbContextFactory(
                new DbContextOptionsBuilder<AppDbContext>().UseSqlite(dbContext.Database.GetDbConnection()).Options),
            workspaceService,
            NullLogger<WorkspaceRepository>.Instance);
    }

    [Fact]
    public async Task Catalog_GetAsync_returns_null_when_workspace_is_missing()
    {
        var (connection, dbContext) = await CreateEmptyDbAsync();
        await using var _ = connection;
        await using var __ = dbContext;

        var catalog = new WorkspaceCatalogOperations(CreateWorkspaceRepository(dbContext));

        Assert.Null(await catalog.GetAsync(999));
    }

    [Fact]
    public async Task FileOperations_ListAsync_returns_null_when_workspace_is_missing()
    {
        var (connection, dbContext) = await CreateEmptyDbAsync();
        await using var _ = connection;
        await using var __ = dbContext;

        var operations = new WorkspaceFileOperations(
            CreateWorkspaceRepository(dbContext),
            new WorkspaceFileRepository(dbContext, NullLogger<WorkspaceFileRepository>.Instance),
            null!,
            null!,
            null!,
            null!);

        Assert.Null(await operations.ListAsync(999, CancellationToken.None));
    }

    [Fact]
    public async Task Catalog_ListAsync_returns_empty_when_there_are_no_workspaces()
    {
        var (connection, dbContext) = await CreateEmptyDbAsync();
        await using var _ = connection;
        await using var __ = dbContext;

        var catalog = new WorkspaceCatalogOperations(CreateWorkspaceRepository(dbContext));

        Assert.Empty(await catalog.ListAsync());
    }

    [Fact]
    public void ToMessageAction_forwards_progress_messages()
    {
        var progress = new ListProgress();
        var report = progress.ToMessageAction();

        report("Restoring packages...");

        Assert.Equal(["Restoring packages..."], progress.Messages);
    }
}