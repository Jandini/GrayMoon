using GrayMoon.App.Components.Features;
using GrayMoon.App.Data;
using GrayMoon.App.Services.Ai;
using GrayMoon.App.Services.Security;
using GrayMoon.App.Services.Worker;
using GrayMoon.Common.Ai;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace GrayMoon.App.Tests;

/// <summary>Reversible stand-in for the connector token protector: the stored text is never the plain value.</summary>
internal sealed class TestTokenProtector : ITokenProtector
{
    private const string Prefix = "test-protected:";

    public string Protect(string plainText) =>
        Prefix + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(plainText));

    public string Unprotect(string protectedValue) =>
        protectedValue.StartsWith(Prefix, StringComparison.Ordinal)
            ? System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(protectedValue[Prefix.Length..]))
            : throw new FormatException("Not a protected value.");

    public string? TryGetKeyId(string protectedValue) => null;
}

public sealed class AgentEnvironmentStoreTests : IAsyncLifetime
{
    private static readonly AgentEnvironmentFix SystemCa = new("NODE_OPTIONS", "--use-system-ca", AppendToExisting: true);

    private SqliteConnection _connection = null!;
    private IDbContextFactory<AppDbContext> _factory = null!;

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        await _connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;
        await using (var db = new AppDbContext(options))
            await db.Database.CreateSchemaAsync();
        _factory = new GitChangesTestDbContext.TestDbContextFactory(options);
    }

    public async Task DisposeAsync() => await _connection.DisposeAsync();

    // A new instance each time: nothing may come from an earlier instance's memory.
    private AgentEnvironmentStore NewStore() =>
        new(_factory, new TestTokenProtector(), NullLogger<AgentEnvironmentStore>.Instance);

    [Fact]
    public async Task A_saved_list_is_read_back_in_order_by_a_new_instance()
    {
        AgentEnvironmentVariable[] list =
        [
            new(AiProviderIds.Cursor, "NODE_OPTIONS", "--use-system-ca"),
            new(AgentEnvironmentScopes.All, "HTTPS_PROXY", "http://proxy:8080"),
            new(AiProviderIds.Claude, "CLAUDE_CODE_DISABLE_BG_SHELL_PRESSURE_REAP", "1"),
            new(AiProviderIds.Claude, "EMPTY", ""),
        ];

        Assert.Null(await NewStore().SaveAsync(list));

        Assert.Equal(list, await NewStore().GetAsync());
    }

    [Fact]
    public async Task Values_are_stored_encrypted()
    {
        await NewStore().SaveAsync([new(AgentEnvironmentScopes.All, "API_KEY", "sk-very-secret")]);

        await using var db = await _factory.CreateDbContextAsync();
        var row = await db.AgentEnvironmentVariables.SingleAsync();
        Assert.Equal("API_KEY", row.Name);
        Assert.DoesNotContain("sk-very-secret", row.ProtectedValue);
    }

    [Fact]
    public async Task A_list_with_a_name_twice_in_a_scope_is_refused_and_the_stored_list_stays()
    {
        var store = NewStore();
        await store.SaveAsync([new(AiProviderIds.Cursor, "KEEP", "1")]);

        var refused = await store.SaveAsync(
        [
            new(AiProviderIds.Cursor, "NODE_OPTIONS", "a"),
            new(AiProviderIds.Cursor, "node_options", "b"),
        ]);

        Assert.Contains("listed twice", refused);
        Assert.Equal("KEEP", Assert.Single(await NewStore().GetAsync()).Name);
    }

    [Fact]
    public async Task The_database_itself_refuses_a_name_twice_in_a_scope_whatever_its_case()
    {
        await using var db = await _factory.CreateDbContextAsync();
        db.AgentEnvironmentVariables.Add(new() { Scope = "cursor", Name = "NODE_OPTIONS", ProtectedValue = "x", Position = 0 });
        db.AgentEnvironmentVariables.Add(new() { Scope = "cursor", Name = "node_options", ProtectedValue = "y", Position = 1 });

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Saving_replaces_the_list_and_an_empty_list_clears_it()
    {
        var store = NewStore();
        await store.SaveAsync([new(AgentEnvironmentScopes.All, "A", "1"), new(AgentEnvironmentScopes.All, "B", "2")]);

        await store.SaveAsync([new(AgentEnvironmentScopes.All, "B", "3")]);
        Assert.Equal([new AgentEnvironmentVariable(AgentEnvironmentScopes.All, "B", "3")], await NewStore().GetAsync());

        await store.SaveAsync([]);
        Assert.Empty(await NewStore().GetAsync());
    }

    [Fact]
    public async Task A_fix_is_stored_once_however_often_it_is_applied()
    {
        var store = NewStore();
        var changes = 0;
        store.Changed += () => changes++;

        var first = await store.ApplyFixAsync(AiProviderIds.Cursor, SystemCa);
        var second = await store.ApplyFixAsync(AiProviderIds.Cursor, SystemCa);

        Assert.Equal(new AgentEnvironmentVariable(AiProviderIds.Cursor, "NODE_OPTIONS", "--use-system-ca"), first);
        Assert.Null(second);
        Assert.Equal(1, changes);
        Assert.Equal([first!], await NewStore().GetAsync());
    }

    [Fact]
    public async Task Fixes_applied_at_the_same_moment_still_store_one_row()
    {
        var store = NewStore();

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => store.ApplyFixAsync(AiProviderIds.Cursor, SystemCa)));

        Assert.Single(results, r => r is not null);
        Assert.Single(await NewStore().GetAsync());
    }

    [Fact]
    public async Task A_fix_keeps_the_rows_the_user_saved()
    {
        var store = NewStore();
        await store.SaveAsync(
        [
            new(AgentEnvironmentScopes.All, "HTTPS_PROXY", "http://proxy:8080"),
            new(AiProviderIds.Cursor, "NODE_OPTIONS", "--max-old-space-size=4096"),
        ]);

        await store.ApplyFixAsync(AiProviderIds.Cursor, SystemCa);

        Assert.Equal(
        [
            new AgentEnvironmentVariable(AgentEnvironmentScopes.All, "HTTPS_PROXY", "http://proxy:8080"),
            new AgentEnvironmentVariable(AiProviderIds.Cursor, "NODE_OPTIONS", "--max-old-space-size=4096 --use-system-ca"),
        ], await NewStore().GetAsync());
    }

    [Fact]
    public async Task Each_agent_resolves_its_own_variables()
    {
        var store = NewStore();
        await store.SaveAsync(
        [
            new(AgentEnvironmentScopes.All, "SHARED", "1"),
            new(AiProviderIds.Cursor, "NODE_OPTIONS", "--use-system-ca"),
        ]);

        Assert.Equal(2, (await store.ResolveAsync(AiProviderIds.Cursor)).Count);
        Assert.Equal(["SHARED"], (await store.ResolveAsync(AiProviderIds.Claude)).Keys);
    }

    [Fact]
    public async Task A_save_tells_subscribers_and_a_refused_save_does_not()
    {
        var store = NewStore();
        var changes = 0;
        store.Changed += () => changes++;

        await store.SaveAsync([new(AgentEnvironmentScopes.All, "A", "1")]);
        await store.SaveAsync([new("nobody", "A", "1")]);

        Assert.Equal(1, changes);
    }

    [Fact]
    public async Task A_row_that_cannot_be_decrypted_is_left_out_and_the_rest_is_read()
    {
        await NewStore().SaveAsync([new(AgentEnvironmentScopes.All, "GOOD", "1")]);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.AgentEnvironmentVariables.Add(new() { Scope = "all", Name = "BROKEN", ProtectedValue = "from another key", Position = 5 });
            await db.SaveChangesAsync();
        }

        Assert.Equal("GOOD", Assert.Single(await NewStore().GetAsync()).Name);
    }

    // ---- Known variables the user already has set on the Worker host are listed at startup ----

    private static object HostInfoWith(string? cursorNodeOptions, string? claudeReap = null, bool cursorInstalled = true) => new
    {
        gitVersion = "2.47.0",
        aiProviders = new object[]
        {
            new
            {
                providerId = AiProviderIds.Cursor,
                isInstalled = cursorInstalled,
                version = "2026.10.16",
                knownEnvironmentVariables = new object[]
                {
                    new { name = "NODE_OPTIONS", value = "--use-system-ca", description = "d", currentValue = cursorNodeOptions },
                },
            },
            new
            {
                providerId = AiProviderIds.Claude,
                isInstalled = true,
                version = "2.1",
                knownEnvironmentVariables = new object[]
                {
                    new { name = "CLAUDE_CODE_DISABLE_BG_SHELL_PRESSURE_REAP", value = "1", description = "d", currentValue = claudeReap },
                },
            },
        },
    };

    private static ScriptedWorkerBridge BridgeAnswering(Func<object> hostInfo) => new()
    {
        Handler = (command, _) => command == GrayMoon.Abstractions.Worker.WorkerHubMethods.GetHostInfo
            ? new GrayMoon.Abstractions.Worker.WorkerCommandResponse(true, hostInfo(), null)
            : new GrayMoon.Abstractions.Worker.WorkerCommandResponse(false, null, $"unexpected {command}"),
    };

    [Fact]
    public async Task The_startup_host_check_lists_the_known_variables_already_set_on_the_host()
    {
        var store = NewStore();
        var cache = new HostInfoCache(NullLogger<HostInfoCache>.Instance, store);
        var bridge = BridgeAnswering(() => HostInfoWith("--max-old-space-size=4096", claudeReap: "1"));

        var query = await cache.GetAsync(bridge, refresh: true, CancellationToken.None);

        Assert.NotNull(query.Read);
        Assert.Equal(
        [
            new AgentEnvironmentVariable(AiProviderIds.Claude, "CLAUDE_CODE_DISABLE_BG_SHELL_PRESSURE_REAP", "1"),
            new AgentEnvironmentVariable(AiProviderIds.Cursor, "NODE_OPTIONS", "--max-old-space-size=4096"),
        ], await NewStore().GetAsync());
    }

    [Fact]
    public async Task A_variable_that_is_not_set_on_the_host_or_whose_cli_is_not_installed_is_not_listed()
    {
        var store = NewStore();
        var cache = new HostInfoCache(NullLogger<HostInfoCache>.Instance, store);

        await cache.GetAsync(BridgeAnswering(() => HostInfoWith("--trace-warnings", claudeReap: null, cursorInstalled: false)), refresh: true, CancellationToken.None);

        Assert.Empty(await NewStore().GetAsync());
    }

    [Fact]
    public async Task The_list_wins_over_the_host_and_no_name_is_listed_twice()
    {
        var store = NewStore();
        await store.SaveAsync([new(AiProviderIds.Cursor, "node_options", "--mine")]);
        var cache = new HostInfoCache(NullLogger<HostInfoCache>.Instance, store);

        await cache.GetAsync(BridgeAnswering(() => HostInfoWith("--from-the-host")), refresh: true, CancellationToken.None);

        Assert.Equal([new AgentEnvironmentVariable(AiProviderIds.Cursor, "node_options", "--mine")], await NewStore().GetAsync());
    }

    [Fact]
    public async Task Only_the_first_host_check_lists_them_so_a_removed_row_stays_removed()
    {
        var store = NewStore();
        var cache = new HostInfoCache(NullLogger<HostInfoCache>.Instance, store);
        var bridge = BridgeAnswering(() => HostInfoWith("--max-old-space-size=4096"));
        await cache.GetAsync(bridge, refresh: true, CancellationToken.None);
        Assert.Single(await store.GetAsync());

        // The user removes the row on the Environment tab; the Worker page then checks the host again.
        await store.SaveAsync([]);
        await cache.GetAsync(bridge, refresh: true, CancellationToken.None);

        Assert.Empty(await NewStore().GetAsync());
    }

    [Fact]
    public async Task A_worker_that_was_offline_at_startup_is_read_at_the_first_check_that_succeeds()
    {
        var store = NewStore();
        var cache = new HostInfoCache(NullLogger<HostInfoCache>.Instance, store);
        var bridge = BridgeAnswering(() => HostInfoWith("--max-old-space-size=4096"));

        bridge.IsWorkerConnected = false;
        Assert.Null((await cache.GetAsync(bridge, refresh: true, CancellationToken.None)).Read);
        Assert.Empty(await store.GetAsync());

        bridge.IsWorkerConnected = true;
        await cache.GetAsync(bridge, refresh: true, CancellationToken.None);

        Assert.Single(await NewStore().GetAsync());
    }

    [Fact]
    public async Task Listing_host_values_tells_subscribers_once_and_not_when_nothing_is_added()
    {
        var store = NewStore();
        var changes = 0;
        store.Changed += () => changes++;
        AgentEnvironmentHostValue[] host = [new(AiProviderIds.Cursor, "NODE_OPTIONS", "--a")];

        Assert.Single(await store.ImportHostValuesAsync(host));
        Assert.Empty(await store.ImportHostValuesAsync(host));
        Assert.Empty(await store.ImportHostValuesAsync([]));

        Assert.Equal(1, changes);
    }

    [Fact]
    public async Task A_host_check_without_a_store_still_answers()
    {
        var cache = new HostInfoCache(NullLogger<HostInfoCache>.Instance);

        var query = await cache.GetAsync(BridgeAnswering(() => HostInfoWith("--a")), refresh: true, CancellationToken.None);

        Assert.Equal("--a", query.Read!.AiProviders.Single(p => p.ProviderId == AiProviderIds.Cursor).HostEnvironmentValues.Single().Value);
    }

    [Fact]
    public async Task The_table_migration_is_strict_step_20_and_idempotent()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var db = new AppDbContext(options);
        await db.Database.CreateSchemaAsync();
        await using (var drop = connection.CreateCommand())
        {
            drop.CommandText = "DROP TABLE \"AgentEnvironmentVariables\"";
            await drop.ExecuteNonQueryAsync();
        }

        var step = Migrations.StrictSteps.Single(s => s.Version == 20);
        await step.Action(db);
        await Migrations.MigrateAgentEnvironmentVariablesAsync(db);

        // The migrated table behaves like the model's: it stores a list and refuses a name twice in a scope.
        var store = new AgentEnvironmentStore(new GitChangesTestDbContext.TestDbContextFactory(options), new TestTokenProtector(), NullLogger<AgentEnvironmentStore>.Instance);
        Assert.Null(await store.SaveAsync([new(AiProviderIds.Cursor, "NODE_OPTIONS", "--use-system-ca")]));
        db.AgentEnvironmentVariables.Add(new() { Scope = "cursor", Name = "node_options", ProtectedValue = "y", Position = 1 });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public void Only_the_ai_open_in_tools_take_environment_variables()
    {
        Assert.Equal(AiProviderIds.Cursor, FeatureOpenInTools.Find(FeatureOpenInTools.Cursor)!.AgentScope);
        Assert.Equal(AiProviderIds.Claude, FeatureOpenInTools.Find(FeatureOpenInTools.ClaudeCli)!.AgentScope);
        Assert.Equal(AiProviderIds.Codex, FeatureOpenInTools.Find(FeatureOpenInTools.CodexCli)!.AgentScope);
        Assert.Null(FeatureOpenInTools.Find(FeatureOpenInTools.VsCode)!.AgentScope);
        Assert.Null(FeatureOpenInTools.Find(FeatureOpenInTools.VisualStudio)!.AgentScope);
        Assert.Null(FeatureOpenInTools.Find(FeatureOpenInTools.Terminal)!.AgentScope);
        Assert.Null(FeatureOpenInTools.Find(FeatureOpenInTools.Explorer)!.AgentScope);
        Assert.All(FeatureOpenInTools.All.Where(t => t.AgentScope is not null), t => Assert.True(AiProviderIds.IsKnown(t.AgentScope)));
    }

    [Fact]
    public void Host_info_reads_each_providers_known_variables_and_drops_unusable_ones()
    {
        var read = HostInfoCache.TryRead(new
        {
            gitVersion = "2.47.0",
            aiProviders = new object[]
            {
                new
                {
                    providerId = AiProviderIds.Cursor,
                    isInstalled = true,
                    version = "2026.10.16",
                    knownEnvironmentVariables = new object[]
                    {
                        new { name = "NODE_OPTIONS", value = "--use-system-ca", description = "Trusts the Windows certificate store." },
                        new { name = "NOT A NAME", value = "1", description = "dropped" },
                    },
                },
                new { providerId = AiProviderIds.Claude, isInstalled = true, version = "2.1" },
            },
        });

        var cursor = read!.AiProviders.Single(p => p.ProviderId == AiProviderIds.Cursor);
        Assert.Equal(
            [new KnownAgentEnvironmentVariable(AiProviderIds.Cursor, "NODE_OPTIONS", "--use-system-ca", "Trusts the Windows certificate store.")],
            cursor.KnownEnvironmentVariables);
        // A Worker answer without the field (an older Worker) is an empty list, not an error.
        Assert.Empty(read.AiProviders.Single(p => p.ProviderId == AiProviderIds.Claude).KnownEnvironmentVariables);
    }
}
