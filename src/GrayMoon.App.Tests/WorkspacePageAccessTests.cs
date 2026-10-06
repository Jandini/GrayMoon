using GrayMoon.Abstractions.Workspaces;
using GrayMoon.App.Data;
using GrayMoon.App.Models;
using GrayMoon.App.Services.Workspaces;
using GrayMoon.Application.Workspaces;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace GrayMoon.App.Tests;

public sealed class WorkspacePageAccessTests
{
    private static WorkspaceCapabilities Caps(WorkspaceType type, WorkspaceCiProvider ci, WorkspaceVersioningMode versioning = WorkspaceVersioningMode.None) =>
        new(type, versioning, ci);

    public static TheoryData<WorkspaceType, WorkspaceVersioningMode, WorkspaceCiProvider> AllProfiles()
    {
        var data = new TheoryData<WorkspaceType, WorkspaceVersioningMode, WorkspaceCiProvider>();
        foreach (var type in Enum.GetValues<WorkspaceType>())
        foreach (var versioning in Enum.GetValues<WorkspaceVersioningMode>())
        foreach (var ci in Enum.GetValues<WorkspaceCiProvider>())
            data.Add(type, versioning, ci);
        return data;
    }

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Repositories_changes_and_files_are_always_available(
        WorkspaceType type, WorkspaceVersioningMode versioning, WorkspaceCiProvider ci)
    {
        var capabilities = new WorkspaceCapabilities(type, versioning, ci);

        Assert.True(WorkspacePageAccess.IsAvailable(WorkspacePage.Repositories, capabilities));
        Assert.True(WorkspacePageAccess.IsAvailable(WorkspacePage.Changes, capabilities));
        Assert.True(WorkspacePageAccess.IsAvailable(WorkspacePage.Files, capabilities));
    }

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Dotnet_pages_follow_the_workspace_type_and_actions_follows_only_the_ci_provider(
        WorkspaceType type, WorkspaceVersioningMode versioning, WorkspaceCiProvider ci)
    {
        var capabilities = new WorkspaceCapabilities(type, versioning, ci);
        var isDotNet = type == WorkspaceType.DotNetDependency;
        var hasCi = ci != WorkspaceCiProvider.None;

        Assert.Equal(isDotNet, WorkspacePageAccess.IsAvailable(WorkspacePage.Projects, capabilities));
        Assert.Equal(isDotNet, WorkspacePageAccess.IsAvailable(WorkspacePage.Packages, capabilities));
        Assert.Equal(isDotNet, WorkspacePageAccess.IsAvailable(WorkspacePage.Dependencies, capabilities));
        Assert.Equal(hasCi, WorkspacePageAccess.IsAvailable(WorkspacePage.Actions, capabilities));
    }

    [Fact]
    public void Basic_with_github_actions_shows_actions_and_dotnet_without_ci_hides_it()
    {
        Assert.True(WorkspacePageAccess.IsAvailable(
            WorkspacePage.Actions, Caps(WorkspaceType.Basic, WorkspaceCiProvider.GitHubActions)));
        Assert.False(WorkspacePageAccess.IsAvailable(
            WorkspacePage.Actions, Caps(WorkspaceType.DotNetDependency, WorkspaceCiProvider.None, WorkspaceVersioningMode.GitVersion)));
    }

    [Fact]
    public void Versioning_does_not_enable_dotnet_pages_for_basic()
    {
        var capabilities = Caps(WorkspaceType.Basic, WorkspaceCiProvider.None, WorkspaceVersioningMode.GitVersion);

        Assert.False(WorkspacePageAccess.IsAvailable(WorkspacePage.Projects, capabilities));
        Assert.False(WorkspacePageAccess.IsAvailable(WorkspacePage.Packages, capabilities));
        Assert.False(WorkspacePageAccess.IsAvailable(WorkspacePage.Dependencies, capabilities));
    }

    [Fact]
    public void Navigation_item_set_for_each_type_and_ci_combination()
    {
        Assert.Equal(
            [WorkspacePage.Repositories, WorkspacePage.Changes, WorkspacePage.Files],
            WorkspacePageAccess.GetAvailablePages(Caps(WorkspaceType.Basic, WorkspaceCiProvider.None)));

        Assert.Equal(
            [WorkspacePage.Repositories, WorkspacePage.Changes, WorkspacePage.Files, WorkspacePage.Actions],
            WorkspacePageAccess.GetAvailablePages(Caps(WorkspaceType.Basic, WorkspaceCiProvider.GitHubActions)));

        Assert.Equal(
            [
                WorkspacePage.Repositories, WorkspacePage.Changes, WorkspacePage.Projects, WorkspacePage.Packages,
                WorkspacePage.Files, WorkspacePage.Dependencies
            ],
            WorkspacePageAccess.GetAvailablePages(Caps(WorkspaceType.DotNetDependency, WorkspaceCiProvider.None)));

        Assert.Equal(
            WorkspacePageAccess.NavigationOrder,
            WorkspacePageAccess.GetAvailablePages(WorkspaceCapabilities.Legacy));
    }

    [Fact]
    public void Missing_workspace_shows_only_always_available_items_and_gated_pages_report_not_found()
    {
        Assert.Equal(
            [WorkspacePage.Repositories, WorkspacePage.Changes, WorkspacePage.Files],
            WorkspacePageAccess.GetAvailablePages(null));

        foreach (var page in WorkspacePageAccess.NavigationOrder)
            Assert.Equal(WorkspacePageAccessOutcome.WorkspaceNotFound, WorkspacePageAccess.Evaluate(page, null));
    }

    [Fact]
    public void Unavailable_messages_are_standard_per_reason()
    {
        Assert.Equal(
            "Packages are only available for .NET Dependency workspaces.",
            WorkspacePageAccess.GetUnavailableMessage(WorkspacePage.Packages, WorkspacePageAccessOutcome.NotAvailable));
        Assert.Equal(
            "CI is not enabled for this workspace.",
            WorkspacePageAccess.GetUnavailableMessage(WorkspacePage.Actions, WorkspacePageAccessOutcome.NotAvailable));
        Assert.Equal(
            "Workspace not found.",
            WorkspacePageAccess.GetUnavailableMessage(WorkspacePage.Projects, WorkspacePageAccessOutcome.WorkspaceNotFound));
        Assert.Equal(
            string.Empty,
            WorkspacePageAccess.GetUnavailableMessage(WorkspacePage.Projects, WorkspacePageAccessOutcome.Available));
    }

    [Theory]
    [InlineData(WorkspacePage.Projects)]
    [InlineData(WorkspacePage.Packages)]
    [InlineData(WorkspacePage.Dependencies)]
    public async Task Basic_workspace_direct_route_is_not_available_and_never_runs_the_page_loader(WorkspacePage page)
    {
        await using var context = await AccessTestContext.CreateAsync();
        var workspaceId = await context.AddWorkspaceAsync(WorkspaceType.Basic, WorkspaceCiProvider.GitHubActions);
        var loaderCalls = 0;

        var outcome = await context.Resolver.LoadIfAvailableAsync(workspaceId, page, () =>
        {
            loaderCalls++;
            return Task.CompletedTask;
        });

        Assert.Equal(WorkspacePageAccessOutcome.NotAvailable, outcome);
        Assert.Equal(0, loaderCalls);
    }

    [Fact]
    public async Task Dotnet_workspace_without_ci_loads_dotnet_pages_but_not_actions()
    {
        await using var context = await AccessTestContext.CreateAsync();
        var workspaceId = await context.AddWorkspaceAsync(WorkspaceType.DotNetDependency, WorkspaceCiProvider.None);
        var loaded = new List<WorkspacePage>();

        foreach (var page in new[] { WorkspacePage.Projects, WorkspacePage.Packages, WorkspacePage.Dependencies, WorkspacePage.Actions })
        {
            await context.Resolver.LoadIfAvailableAsync(workspaceId, page, () =>
            {
                loaded.Add(page);
                return Task.CompletedTask;
            });
        }

        Assert.Equal([WorkspacePage.Projects, WorkspacePage.Packages, WorkspacePage.Dependencies], loaded);
        Assert.Equal(
            WorkspacePageAccessOutcome.NotAvailable,
            await context.Resolver.CheckAsync(workspaceId, WorkspacePage.Actions));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    [InlineData(987654)]
    public async Task Missing_workspace_id_is_an_outcome_not_an_exception(int workspaceId)
    {
        await using var context = await AccessTestContext.CreateAsync();
        var loaderCalls = 0;

        var outcome = await context.Resolver.LoadIfAvailableAsync(workspaceId, WorkspacePage.Packages, () =>
        {
            loaderCalls++;
            return Task.CompletedTask;
        });
        var navPages = await context.Resolver.GetAvailablePagesAsync(workspaceId);

        Assert.Equal(WorkspacePageAccessOutcome.WorkspaceNotFound, outcome);
        Assert.Equal(0, loaderCalls);
        Assert.Equal([WorkspacePage.Repositories, WorkspacePage.Changes, WorkspacePage.Files], navPages);
    }

    [Fact]
    public async Task Navigation_pages_are_resolved_from_the_persisted_profile()
    {
        await using var context = await AccessTestContext.CreateAsync();
        var basicWithCi = await context.AddWorkspaceAsync(WorkspaceType.Basic, WorkspaceCiProvider.GitHubActions);
        var dotnetNoCi = await context.AddWorkspaceAsync(WorkspaceType.DotNetDependency, WorkspaceCiProvider.None);

        Assert.Equal(
            [WorkspacePage.Repositories, WorkspacePage.Changes, WorkspacePage.Files, WorkspacePage.Actions],
            await context.Resolver.GetAvailablePagesAsync(basicWithCi));
        Assert.DoesNotContain(WorkspacePage.Actions, await context.Resolver.GetAvailablePagesAsync(dotnetNoCi));
        Assert.Contains(WorkspacePage.Packages, await context.Resolver.GetAvailablePagesAsync(dotnetNoCi));
    }

    [Fact]
    public async Task Loader_exceptions_propagate_to_the_page()
    {
        await using var context = await AccessTestContext.CreateAsync();
        var workspaceId = await context.AddWorkspaceAsync(WorkspaceType.DotNetDependency, WorkspaceCiProvider.GitHubActions);

        await Assert.ThrowsAsync<InvalidOperationException>(() => context.Resolver.LoadIfAvailableAsync(
            workspaceId, WorkspacePage.Projects, () => throw new InvalidOperationException("load failed")));
    }

    private sealed class AccessTestContext : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly AppDbContext _dbContext;

        public WorkspacePageAccessResolver Resolver { get; }

        private AccessTestContext(SqliteConnection connection, AppDbContext dbContext, DbContextOptions<AppDbContext> options)
        {
            _connection = connection;
            _dbContext = dbContext;
            Resolver = new WorkspacePageAccessResolver(new WorkspaceCapabilitiesResolver(new TestDbContextFactory(options)));
        }

        public static async Task<AccessTestContext> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();

            var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
            var dbContext = new AppDbContext(options);
            await dbContext.Database.EnsureCreatedAsync();

            return new AccessTestContext(connection, dbContext, options);
        }

        public async Task<int> AddWorkspaceAsync(WorkspaceType type, WorkspaceCiProvider ciProvider)
        {
            var workspace = new Workspace
            {
                Name = $"ws-{Guid.NewGuid():N}",
                Type = type,
                VersioningMode = type == WorkspaceType.DotNetDependency
                    ? WorkspaceVersioningMode.GitVersion
                    : WorkspaceVersioningMode.None,
                CiProvider = ciProvider
            };
            _dbContext.Workspaces.Add(workspace);
            await _dbContext.SaveChangesAsync();
            return workspace.WorkspaceId;
        }

        public async ValueTask DisposeAsync()
        {
            await _dbContext.DisposeAsync();
            await _connection.DisposeAsync();
        }

        private sealed class TestDbContextFactory(DbContextOptions<AppDbContext> options) : IDbContextFactory<AppDbContext>
        {
            public AppDbContext CreateDbContext() => new(options);
        }
    }
}
