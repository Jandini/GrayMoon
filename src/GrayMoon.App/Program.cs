using System.Reflection;
using GrayMoon.App.Api;
using GrayMoon.App.Components;
using GrayMoon.App.Data;
using GrayMoon.App.Hubs;
using GrayMoon.App.Models;
using GrayMoon.App.Repositories;
using GrayMoon.App.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Serilog;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Host.UseSerilog((context, services, configuration) => configuration
    .ReadFrom.Configuration(context.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext());
builder.Configuration.AddEnvironmentVariables();

builder.Services.Configure<WorkspaceOptions>(builder.Configuration.GetSection("Workspace"));

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Database (SQLite) for persisted data - stored in db/ for easy container volume mounting
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection") ?? "Data Source=db/graymoon.db";
builder.Services.AddDbContext<AppDbContext>(options => options.UseSqlite(connectionString));
builder.Services.AddScoped<ConnectorRepository>();
builder.Services.AddScoped<GitHubRepositoryRepository>();
builder.Services.AddScoped<WorkspaceProjectRepository>();
builder.Services.AddScoped<WorkspaceRepository>();
builder.Services.AddSingleton<AgentConnectionTracker>();
builder.Services.AddScoped<SyncCommandHandler>();
builder.Services.AddScoped<IAgentBridge, AgentBridge>();
builder.Services.AddScoped<WorkspaceService>();
builder.Services.AddScoped<WorkspaceGitService>();
builder.Services.AddScoped<GitHubRepositoryService>();
builder.Services.AddScoped<GitHubActionsService>();

// Background sync service with controlled parallelism
builder.Services.AddSingleton<SyncBackgroundService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<SyncBackgroundService>());

// GitHub API service
builder.Services.AddHttpClient<GitHubService>();

// Persist Data Protection keys to db volume so antiforgery and other protected data survive container restarts
var keyRingDir = Path.Combine(Path.GetDirectoryName(GetDatabasePath(connectionString) ?? "db") ?? "db", "DataProtection-Keys");
builder.Services.AddDataProtection()
    .SetApplicationName("GrayMoon")
    .PersistKeysToFileSystem(new DirectoryInfo(keyRingDir));

var app = builder.Build();

var version = Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
app.Logger.LogInformation("Starting GrayMoon {Version}...", version);

// Ensure the db directory and Data Protection key directory exist (for both local dev and container volume mounts)
var dbPath = GetDatabasePath(connectionString);
if (!string.IsNullOrEmpty(dbPath))
{
    var dbDir = Path.GetDirectoryName(dbPath);
    if (!string.IsNullOrEmpty(dbDir))
    {
        Directory.CreateDirectory(dbDir);
        Directory.CreateDirectory(Path.Combine(dbDir, "DataProtection-Keys"));
    }
}

// Ensure the local SQLite database is created and migrate schema if needed
using (var scope = app.Services.CreateScope())
{
    var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    dbContext.Database.EnsureCreated();
    await MigrateWorkspaceSyncMetadataAsync(dbContext);
    await MigrateConnectorUserNameAsync(dbContext);
    await MigrateWorkspaceRepositoriesSyncStatusAsync(dbContext);
    await MigrateWorkspaceRepositoriesProjectsAsync(dbContext);
    await MigrateWorkspaceRepositoriesCommitsAsync(dbContext);
    await MigrateWorkspaceRepositoriesSequenceAndDependenciesAsync(dbContext);
}

static string? GetDatabasePath(string connectionString)
{
    const string prefix = "Data Source=";
    var idx = connectionString.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
    if (idx < 0) return null;
    var path = connectionString[(idx + prefix.Length)..].Trim();
    return string.IsNullOrEmpty(path) ? null : path;
}

static async Task MigrateWorkspaceSyncMetadataAsync(AppDbContext dbContext)
{
    try
    {
        var conn = dbContext.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
            await conn.OpenAsync();

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('Workspaces') WHERE name='LastSyncedAt'";
            var count = Convert.ToInt32(await cmd.ExecuteScalarAsync());
            if (count == 0)
            {
                cmd.CommandText = "ALTER TABLE Workspaces ADD COLUMN LastSyncedAt TEXT";
                await cmd.ExecuteNonQueryAsync();
                cmd.CommandText = "ALTER TABLE Workspaces ADD COLUMN IsInSync INTEGER NOT NULL DEFAULT 0";
                await cmd.ExecuteNonQueryAsync();
            }
        }
    }
    catch
    {
        // Migration may already be applied or table doesn't exist yet
    }
}

static async Task MigrateConnectorUserNameAsync(AppDbContext dbContext)
{
    try
    {
        var conn = dbContext.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
            await conn.OpenAsync();

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('Connectors') WHERE name='UserName'";
            var count = Convert.ToInt32(await cmd.ExecuteScalarAsync());
            if (count == 0)
            {
                cmd.CommandText = "ALTER TABLE Connectors ADD COLUMN UserName TEXT";
                await cmd.ExecuteNonQueryAsync();
            }
        }
    }
    catch
    {
        // Migration may already be applied or table doesn't exist yet
    }
}

static async Task MigrateWorkspaceRepositoriesSyncStatusAsync(AppDbContext dbContext)
{
    try
    {
        var conn = dbContext.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
            await conn.OpenAsync();

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('WorkspaceRepositories') WHERE name='SyncStatus'";
            var count = Convert.ToInt32(await cmd.ExecuteScalarAsync());
            if (count == 0)
            {
                // RepoSyncStatus.NeedsSync = 4; default new/unknown repos to needs sync
                cmd.CommandText = "ALTER TABLE WorkspaceRepositories ADD COLUMN SyncStatus INTEGER NOT NULL DEFAULT 4";
                await cmd.ExecuteNonQueryAsync();
            }
        }
    }
    catch
    {
        // Migration may already be applied or table doesn't exist yet
    }
}

static async Task MigrateWorkspaceRepositoriesProjectsAsync(AppDbContext dbContext)
{
    try
    {
        var conn = dbContext.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
            await conn.OpenAsync();

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('WorkspaceRepositories') WHERE name='Projects'";
            var count = Convert.ToInt32(await cmd.ExecuteScalarAsync());
            if (count == 0)
            {
                cmd.CommandText = "ALTER TABLE WorkspaceRepositories ADD COLUMN Projects INTEGER";
                await cmd.ExecuteNonQueryAsync();
            }
        }
    }
    catch
    {
        // Migration may already be applied or table doesn't exist yet
    }
}

static async Task MigrateWorkspaceRepositoriesCommitsAsync(AppDbContext dbContext)
{
    try
    {
        var conn = dbContext.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
            await conn.OpenAsync();

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('WorkspaceRepositories') WHERE name='OutgoingCommits'";
            var count = Convert.ToInt32(await cmd.ExecuteScalarAsync());
            if (count == 0)
            {
                cmd.CommandText = "ALTER TABLE WorkspaceRepositories ADD COLUMN OutgoingCommits INTEGER";
                await cmd.ExecuteNonQueryAsync();
            }
            cmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('WorkspaceRepositories') WHERE name='IncomingCommits'";
            count = Convert.ToInt32(await cmd.ExecuteScalarAsync());
            if (count == 0)
            {
                cmd.CommandText = "ALTER TABLE WorkspaceRepositories ADD COLUMN IncomingCommits INTEGER";
                await cmd.ExecuteNonQueryAsync();
            }
        }
    }
    catch
    {
        // Migration may already be applied or table doesn't exist yet
    }
}

static async Task MigrateWorkspaceRepositoriesSequenceAndDependenciesAsync(AppDbContext dbContext)
{
    try
    {
        var conn = dbContext.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
            await conn.OpenAsync();

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('WorkspaceRepositories') WHERE name='Sequence'";
            var count = Convert.ToInt32(await cmd.ExecuteScalarAsync());
            if (count == 0)
            {
                cmd.CommandText = "ALTER TABLE WorkspaceRepositories ADD COLUMN Sequence INTEGER";
                await cmd.ExecuteNonQueryAsync();
            }
            cmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('WorkspaceRepositories') WHERE name='Dependencies'";
            count = Convert.ToInt32(await cmd.ExecuteScalarAsync());
            if (count == 0)
            {
                cmd.CommandText = "ALTER TABLE WorkspaceRepositories ADD COLUMN Dependencies INTEGER";
                await cmd.ExecuteNonQueryAsync();
            }
            cmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('WorkspaceRepositories') WHERE name='UnmatchedDeps'";
            count = Convert.ToInt32(await cmd.ExecuteScalarAsync());
            if (count == 0)
            {
                cmd.CommandText = "ALTER TABLE WorkspaceRepositories ADD COLUMN UnmatchedDeps INTEGER";
                await cmd.ExecuteNonQueryAsync();
            }
        }
    }
    catch
    {
        // Migration may already be applied or table doesn't exist yet
    }
}

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

// Only redirect to HTTPS when URLs include HTTPS (skip in container when only HTTP is used)
if ((app.Configuration["ASPNETCORE_URLS"] ?? "").Contains("https", StringComparison.OrdinalIgnoreCase))
{
    app.UseHttpsRedirection();
}

app.UseStaticFiles();
app.UseAntiforgery();

app.MapApiEndpoints();
app.MapHub<WorkspaceSyncHub>("/hubs/workspace-sync");
app.MapHub<AgentHub>("/agent");

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

await app.RunAsync();
return 0;
}
catch (Exception ex)
{
    Log.Fatal(ex, "Application terminated unexpectedly");
    return 1;
}
finally
{
    await Log.CloseAndFlushAsync();
}
