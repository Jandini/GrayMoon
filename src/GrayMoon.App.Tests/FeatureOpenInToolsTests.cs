using GrayMoon.App;
using GrayMoon.App.Components.Features;
using GrayMoon.App.Data;
using GrayMoon.App.Models;
using GrayMoon.App.Services.Ui;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace GrayMoon.App.Tests;

public sealed class FeatureOpenInToolsTests
{
    [Fact]
    public void Visible_buttons_are_empty_until_a_tool_is_used()
    {
        var buttons = FeatureOpenInTools.VisibleButtons([], cursor: true, claudeCli: true, vsCode: true, visualStudio: true);

        Assert.Empty(buttons);
    }

    [Fact]
    public void Record_use_appends_a_new_tool_and_leaves_placed_tools_where_they_are()
    {
        var recent = FeatureOpenInTools.RecordUse([], FeatureOpenInTools.Cursor);
        recent = FeatureOpenInTools.RecordUse(recent, FeatureOpenInTools.Terminal);
        recent = FeatureOpenInTools.RecordUse(recent, FeatureOpenInTools.Explorer);
        var again = FeatureOpenInTools.RecordUse(recent, FeatureOpenInTools.Cursor);
        var ignored = FeatureOpenInTools.RecordUse(again, "not-a-tool");

        Assert.Equal(
            [FeatureOpenInTools.Cursor, FeatureOpenInTools.Terminal, FeatureOpenInTools.Explorer],
            recent);
        Assert.Same(recent, again);
        Assert.Same(again, ignored);
    }

    [Fact]
    public void Visible_buttons_hide_unavailable_ides_and_keep_recent_order()
    {
        var recent = new[]
        {
            FeatureOpenInTools.Explorer,
            FeatureOpenInTools.VsCode,
            FeatureOpenInTools.Cursor,
            FeatureOpenInTools.Terminal,
        };
        var buttons = FeatureOpenInTools.VisibleButtons(recent, cursor: false, claudeCli: true, vsCode: true, visualStudio: false);

        Assert.Equal(
            [FeatureOpenInTools.Explorer, FeatureOpenInTools.VsCode, FeatureOpenInTools.Terminal],
            buttons);
    }

    [Fact]
    public void Remove_drops_one_tool_and_keeps_the_rest_in_order()
    {
        var recent = new[]
        {
            FeatureOpenInTools.Cursor,
            FeatureOpenInTools.Terminal,
            FeatureOpenInTools.Explorer,
        };

        var removed = FeatureOpenInTools.Remove(recent, FeatureOpenInTools.Terminal);
        var missing = FeatureOpenInTools.Remove(removed, FeatureOpenInTools.VsCode);
        var unknown = FeatureOpenInTools.Remove(recent, "not-a-tool");

        Assert.Equal([FeatureOpenInTools.Cursor, FeatureOpenInTools.Explorer], removed);
        Assert.Same(removed, missing);
        Assert.Same(recent, unknown);
    }
}

public sealed class WorkspaceOpenInRecentToolsDbTests
{
    [Fact]
    public async Task Record_appends_and_a_new_circuit_reads_the_same_order()
    {
        await using var db = await RecentToolsDb.CreateAsync();
        var workspaceId = await db.SeedWorkspaceAsync();
        var first = db.CreateService();

        var tools = await first.RecordAsync(workspaceId, FeatureOpenInTools.Cursor);
        tools = await first.RecordAsync(workspaceId, FeatureOpenInTools.Terminal);
        var again = await first.RecordAsync(workspaceId, FeatureOpenInTools.Cursor);
        var ignored = await first.RecordAsync(workspaceId, "not-a-tool");

        Assert.Equal([FeatureOpenInTools.Cursor, FeatureOpenInTools.Terminal], tools);
        Assert.Equal(tools, again);
        Assert.Equal(tools, ignored);
        Assert.True(first.TryGetCached(workspaceId, out var cached));
        Assert.Equal(tools, cached);
        await using (var ctx = db.CreateContext())
            Assert.Equal(2, await ctx.WorkspaceOpenInRecentTools.CountAsync());

        var second = db.CreateService();
        Assert.Equal([FeatureOpenInTools.Cursor, FeatureOpenInTools.Terminal], await second.GetAsync(workspaceId));
    }

    [Fact]
    public async Task Remove_deletes_one_tool_and_deleting_the_workspace_clears_the_rest()
    {
        await using var db = await RecentToolsDb.CreateAsync();
        var workspaceId = await db.SeedWorkspaceAsync();
        var recent = db.CreateService();
        await recent.RecordAsync(workspaceId, FeatureOpenInTools.Explorer);
        await recent.RecordAsync(workspaceId, FeatureOpenInTools.VsCode);

        var left = await recent.RemoveAsync(workspaceId, FeatureOpenInTools.Explorer);

        Assert.Equal([FeatureOpenInTools.VsCode], left);
        Assert.Equal([FeatureOpenInTools.VsCode], await db.CreateService().GetAsync(workspaceId));

        await using (var ctx = db.CreateContext())
        {
            var workspace = await ctx.Workspaces.SingleAsync();
            ctx.Workspaces.Remove(workspace);
            await ctx.SaveChangesAsync();
            Assert.Empty(await ctx.WorkspaceOpenInRecentTools.ToListAsync());
        }
    }

    [Fact]
    public async Task Strict_step_7_creates_the_recent_tools_table_once()
    {
        await using var db = await RecentToolsDb.CreateAsync();
        await using var ctx = db.CreateContext();
        await ctx.Database.OpenConnectionAsync();
        var conn = ctx.Database.GetDbConnection();
        await using (var drop = conn.CreateCommand())
        {
            drop.CommandText = "DROP TABLE \"WorkspaceOpenInRecentTools\";";
            await drop.ExecuteNonQueryAsync();
        }

        var step = Migrations.StrictSteps.Single(s => s.Version == 7);
        Assert.Contains(Migrations.StrictSteps, s => s.Version == 6);
        await step.Action(ctx);
        await step.Action(ctx);

        await using var check = conn.CreateCommand();
        check.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'WorkspaceOpenInRecentTools'";
        Assert.Equal(1, Convert.ToInt32(await check.ExecuteScalarAsync()));
    }

    private sealed class RecentToolsDb : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly DbContextOptions<AppDbContext> _options;

        private RecentToolsDb(SqliteConnection connection, DbContextOptions<AppDbContext> options)
        {
            _connection = connection;
            _options = options;
        }

        public static async Task<RecentToolsDb> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            await using (var pragma = connection.CreateCommand())
            {
                pragma.CommandText = "PRAGMA foreign_keys = ON;";
                await pragma.ExecuteNonQueryAsync();
            }

            var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
            await using (var db = new AppDbContext(options))
                await db.Database.EnsureCreatedAsync();
            return new RecentToolsDb(connection, options);
        }

        public WorkspaceOpenInRecentTools CreateService() => new(new Factory(_options));

        public AppDbContext CreateContext() => new(_options);

        public async Task<int> SeedWorkspaceAsync()
        {
            await using var db = CreateContext();
            var workspace = new Workspace { Name = "ws" };
            db.Workspaces.Add(workspace);
            await db.SaveChangesAsync();
            return workspace.WorkspaceId;
        }

        public async ValueTask DisposeAsync() => await _connection.DisposeAsync();

        private sealed class Factory(DbContextOptions<AppDbContext> options) : IDbContextFactory<AppDbContext>
        {
            public AppDbContext CreateDbContext() => new(options);

            public Task<AppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
                Task.FromResult(CreateDbContext());
        }
    }
}
