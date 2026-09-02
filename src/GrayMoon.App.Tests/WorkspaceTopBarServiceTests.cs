using GrayMoon.Abstractions.Agent;
using GrayMoon.App.Data;
using GrayMoon.App.Hubs;
using GrayMoon.App.Models;
using GrayMoon.App.Repositories;
using GrayMoon.App.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GrayMoon.App.Tests;

public sealed class WorkspaceTopBarServiceTests
{
    private sealed class TestNavigationManager : NavigationManager
    {
        public TestNavigationManager(string uri) => Initialize("http://localhost/", uri);

        public void SetUri(string uri)
        {
            Uri = ToAbsoluteUri(uri).ToString();
            NotifyLocationChanged(false);
        }

        protected override void NavigateToCore(string uri, NavigationOptions options) => SetUri(uri);
    }

    private sealed class NoOpAgentBridge : IAgentBridge
    {
        public bool IsAgentConnected => false;

        public Task<AgentCommandResponse> SendCommandAsync(string command, object args, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Not used by these tests.");
    }

    private static async Task<(SqliteConnection Connection, AppDbContext DbContext, int WorkspaceId)> CreateSeededDbAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        var dbContext = new AppDbContext(options);
        await dbContext.Database.EnsureCreatedAsync();

        var workspace = new Workspace { Name = "Acme Workspace" };
        dbContext.Workspaces.Add(workspace);
        await dbContext.SaveChangesAsync();

        return (connection, dbContext, workspace.WorkspaceId);
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
    public async Task Navigating_into_a_workspace_pushes_WorkspaceChanged_with_its_name()
    {
        var (connection, dbContext, workspaceId) = await CreateSeededDbAsync();
        await using var _ = connection;
        await using var __ = dbContext;

        var navigationManager = new TestNavigationManager($"http://localhost/workspaces/{workspaceId}/repositories");
        var hubContext = new FakeHubContext<DesktopNotificationHub>();
        var tracker = new DesktopWorkspaceContextTracker();
        var service = new WorkspaceTopBarService(navigationManager, CreateWorkspaceRepository(dbContext), hubContext, tracker);

        await service.EnsureSynchronizedAsync();

        Assert.Equal("Acme Workspace", service.WorkspaceDisplayName);

        var sent = Assert.Single(hubContext.ClientsImpl.AllProxy.Sent, s => s.Method == "WorkspaceChanged");
        var context = Assert.IsType<WorkspaceContext>(sent.Args[0]);
        Assert.Equal(workspaceId, context.WorkspaceId);
        Assert.Equal("Acme Workspace", context.WorkspaceName);
        Assert.Same(context, tracker.Current);
    }

    [Fact]
    public async Task Navigating_away_from_a_workspace_pushes_WorkspaceChanged_with_null_name()
    {
        var (connection, dbContext, workspaceId) = await CreateSeededDbAsync();
        await using var _ = connection;
        await using var __ = dbContext;

        var navigationManager = new TestNavigationManager($"http://localhost/workspaces/{workspaceId}/repositories");
        var hubContext = new FakeHubContext<DesktopNotificationHub>();
        var tracker = new DesktopWorkspaceContextTracker();
        var service = new WorkspaceTopBarService(navigationManager, CreateWorkspaceRepository(dbContext), hubContext, tracker);

        await service.EnsureSynchronizedAsync();
        hubContext.ClientsImpl.AllProxy.Sent.Clear();

        navigationManager.SetUri("http://localhost/dashboard");
        // LocationChanged handler fires fire-and-forget; EnsureSynchronizedAsync is the same code path
        // and is awaitable, so drive it directly to avoid a race on the background task in the test.
        await service.EnsureSynchronizedAsync();

        Assert.Null(service.WorkspaceDisplayName);

        var sent = Assert.Single(hubContext.ClientsImpl.AllProxy.Sent, s => s.Method == "WorkspaceChanged");
        var context = Assert.IsType<WorkspaceContext>(sent.Args[0]);
        Assert.Null(context.WorkspaceId);
        Assert.Null(context.WorkspaceName);
    }

    [Fact]
    public async Task Non_workspace_route_resets_stale_tracker_from_a_previous_circuit()
    {
        // Simulates a fresh circuit (e.g. after an F5 hard refresh or a WebView2 full navigation)
        // starting on a non-workspace route while the shared singleton tracker still holds the
        // last workspace selected by a previous circuit.
        var (connection, dbContext, workspaceId) = await CreateSeededDbAsync();
        await using var _ = connection;
        await using var __ = dbContext;

        var navigationManager = new TestNavigationManager("http://localhost/dashboard");
        var hubContext = new FakeHubContext<DesktopNotificationHub>();
        var tracker = new DesktopWorkspaceContextTracker
        {
            Current = new WorkspaceContext(workspaceId, "Acme Workspace")
        };
        var service = new WorkspaceTopBarService(navigationManager, CreateWorkspaceRepository(dbContext), hubContext, tracker);

        await service.EnsureSynchronizedAsync();

        Assert.Null(service.WorkspaceDisplayName);

        var sent = Assert.Single(hubContext.ClientsImpl.AllProxy.Sent, s => s.Method == "WorkspaceChanged");
        var context = Assert.IsType<WorkspaceContext>(sent.Args[0]);
        Assert.Null(context.WorkspaceId);
        Assert.Null(context.WorkspaceName);
        Assert.NotNull(tracker.Current);
        Assert.Null(tracker.Current!.WorkspaceName);
    }

    [Fact]
    public async Task Non_workspace_route_never_pushes_when_nothing_was_ever_selected()
    {
        var (connection, dbContext, _) = await CreateSeededDbAsync();
        await using var _1 = connection;
        await using var _2 = dbContext;

        var navigationManager = new TestNavigationManager("http://localhost/dashboard");
        var hubContext = new FakeHubContext<DesktopNotificationHub>();
        var tracker = new DesktopWorkspaceContextTracker();
        var service = new WorkspaceTopBarService(navigationManager, CreateWorkspaceRepository(dbContext), hubContext, tracker);

        await service.EnsureSynchronizedAsync();

        Assert.Null(service.WorkspaceDisplayName);
        Assert.Empty(hubContext.ClientsImpl.AllProxy.Sent);
        Assert.Null(tracker.Current);
    }
}
