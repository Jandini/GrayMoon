using GrayMoon.App.Components.Pages;
using GrayMoon.App.Data;
using GrayMoon.App.Hubs;
using GrayMoon.App.Models;
using GrayMoon.App.Services.Worker;
using Microsoft.AspNetCore.Components;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace GrayMoon.App.Tests;

/// <summary>
/// The Agent -> Worker rename must not break anything that already exists in the field: Workers installed with
/// the old hub URL, bookmarks to the old page, the old configuration section and the existing database column.
/// </summary>
public sealed class WorkerRenameCompatibilityTests
{
    [Theory]
    [InlineData("/hub/worker", true)]
    [InlineData("/hub/worker/negotiate", true)]
    [InlineData("/hub/agent", true)]
    [InlineData("/HUB/AGENT/negotiate", true)]
    [InlineData("/hubs/workspace-sync", false)]
    [InlineData("/api/worker/download", false)]
    public void Worker_hub_is_reachable_on_current_and_legacy_paths(string path, bool expected)
        => Assert.Equal(expected, WorkerHubRoutes.IsWorkerHubPath(path));

    [Fact]
    public void Worker_page_keeps_the_legacy_agent_route()
    {
        var routes = typeof(Worker).GetCustomAttributes(typeof(RouteAttribute), inherit: false)
            .Cast<RouteAttribute>()
            .Select(r => r.Template)
            .ToArray();

        Assert.Contains("/worker", routes);
        Assert.Contains("/agent", routes);
    }

    [Fact]
    public void Legacy_AgentBridge_section_is_still_honored()
    {
        var options = Resolve(new Dictionary<string, string?> { ["AgentBridge:CommandTimeoutSeconds"] = "77" });

        Assert.Equal(77, options.CommandTimeoutSeconds);
    }

    [Fact]
    public void WorkerBridge_section_wins_over_the_legacy_section()
    {
        var options = Resolve(new Dictionary<string, string?>
        {
            ["AgentBridge:CommandTimeoutSeconds"] = "77",
            ["WorkerBridge:CommandTimeoutSeconds"] = "99",
        });

        Assert.Equal(99, options.CommandTimeoutSeconds);
    }

    [Fact]
    public void Default_timeout_is_unchanged_when_neither_section_is_set()
        => Assert.Equal(240, Resolve(new Dictionary<string, string?>()).CommandTimeoutSeconds);

    [Fact]
    public async Task Scan_timestamp_is_still_stored_in_the_AgentScannedAt_column()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var dbContext = new AppDbContext(options);
        await dbContext.Database.EnsureCreatedAsync();

        Assert.Equal("AgentScannedAt", ColumnName(dbContext, typeof(WorkspaceGitRepositoryStatus)));
        Assert.Equal("AgentScannedAt", ColumnName(dbContext, typeof(WorkspaceGitContextRepositoryStatus)));

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM pragma_table_info('WorkspaceGitRepositoryStatus') WHERE name = 'AgentScannedAt'";
        Assert.Equal(1L, await command.ExecuteScalarAsync());
    }

    private static string? ColumnName(AppDbContext dbContext, Type entityType)
    {
        var entity = dbContext.Model.FindEntityType(entityType)!;
        var property = entity.FindProperty(nameof(WorkspaceGitRepositoryStatus.WorkerScannedAt))!;
        return property.GetColumnName();
    }

    private static WorkerBridgeOptions Resolve(Dictionary<string, string?> values)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection();
        // Same registration order as Program.cs: legacy section first, current section last.
        services.Configure<WorkerBridgeOptions>(configuration.GetSection(WorkerBridgeOptions.LegacySectionName));
        services.Configure<WorkerBridgeOptions>(configuration.GetSection(WorkerBridgeOptions.SectionName));
        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOptions<WorkerBridgeOptions>>().Value;
    }
}