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
builder.Services.AddScoped<WorkspaceFileRepository>();    builder.Services.AddScoped<WorkspaceFileVersionConfigRepository>();builder.Services.AddScoped<WorkspaceRepository>();
builder.Services.AddScoped<AppSettingRepository>();
builder.Services.AddSingleton<AgentConnectionTracker>();
builder.Services.AddSingleton<IToastService, ToastService>();
builder.Services.AddSingleton<MatrixOverlayPreferenceService>();
builder.Services.AddScoped<SyncCommandHandler>();
builder.Services.AddScoped<IAgentBridge, AgentBridge>();
builder.Services.AddScoped<WorkspaceService>();
builder.Services.AddScoped<WorkspaceGitService>();
builder.Services.AddScoped<GitHubRepositoryService>();
builder.Services.AddScoped<GitHubActionsService>();
builder.Services.AddScoped<PackageRegistrySyncService>();
builder.Services.AddScoped<IWorkspaceFileSearchService, WorkspaceFileSearchService>();
    builder.Services.AddScoped<WorkspaceFileVersionService>();

// Background sync service with controlled parallelism
builder.Services.AddSingleton<SyncBackgroundService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<SyncBackgroundService>());

// Connector services
builder.Services.AddHttpClient<GitHubService>();
builder.Services.AddHttpClient<NuGetService>();
builder.Services.AddScoped<ConnectorServiceFactory>();

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
    await MigrateRepositoriesTopicsAsync(dbContext);
    await MigrateWorkspaceSyncMetadataAsync(dbContext);
    await MigrateConnectorUserNameAsync(dbContext);
    await MigrateConnectorTypeAndTokenAsync(dbContext);
    await MigrateWorkspaceRepositoriesSyncStatusAsync(dbContext);
    await MigrateWorkspaceRepositoriesProjectsAsync(dbContext);
    await MigrateWorkspaceRepositoriesCommitsAsync(dbContext);
    await MigrateWorkspaceRepositoriesDependencyLevelAndDependenciesAsync(dbContext);
    await MigrateWorkspaceProjectsMatchedConnectorAsync(dbContext);
    await MigrateRepositoryBranchesAsync(dbContext);
    await MigrateRepositoryBranchesIsDefaultAsync(dbContext);
    await MigrateWorkspaceFilesAsync(dbContext);
    await MigrateWorkspaceFileVersionConfigsAsync(dbContext);
    await MigrateSettingsAsync(dbContext);
    await MigrateWorkspaceRootPathAsync(dbContext);
}

static async Task MigrateWorkspaceProjectsMatchedConnectorAsync(AppDbContext dbContext)
{
    try
    {
        var conn = dbContext.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
            await conn.OpenAsync();

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('WorkspaceProjects') WHERE name='MatchedConnectorId'";
            var count = Convert.ToInt32(await cmd.ExecuteScalarAsync());
            if (count == 0)
            {
                cmd.CommandText = "ALTER TABLE WorkspaceProjects ADD COLUMN MatchedConnectorId INTEGER REFERENCES Connectors(ConnectorId)";
                await cmd.ExecuteNonQueryAsync();
            }
        }
    }
    catch
    {
        // Migration may already be applied or table doesn't exist yet
    }
}

static string? GetDatabasePath(string connectionString)
{
    const string prefix = "Data Source=";
    var idx = connectionString.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
    if (idx < 0) return null;
    var path = connectionString[(idx + prefix.Length)..].Trim();
    return string.IsNullOrEmpty(path) ? null : path;
}

static async Task MigrateRepositoriesTopicsAsync(AppDbContext dbContext)
{
    try
    {
        var conn = dbContext.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
            await conn.OpenAsync();

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('Repositories') WHERE name='Topics'";
            var count = Convert.ToInt32(await cmd.ExecuteScalarAsync());
            if (count == 0)
            {
                cmd.CommandText = "ALTER TABLE Repositories ADD COLUMN Topics TEXT";
                await cmd.ExecuteNonQueryAsync();
            }
        }
    }
    catch
    {
        // Migration may already be applied or table doesn't exist yet
    }
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

static async Task MigrateConnectorTypeAndTokenAsync(AppDbContext dbContext)
{
    try
    {
        var conn = dbContext.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
            await conn.OpenAsync();

        await using (var cmd = conn.CreateCommand())
        {
            // Add ConnectorType column if it doesn't exist
            cmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('Connectors') WHERE name='ConnectorType'";
            var count = Convert.ToInt32(await cmd.ExecuteScalarAsync());
            if (count == 0)
            {
                // Add ConnectorType column with default value of 1 (GitHub)
                cmd.CommandText = "ALTER TABLE Connectors ADD COLUMN ConnectorType INTEGER NOT NULL DEFAULT 1";
                await cmd.ExecuteNonQueryAsync();
            }

            // Make UserToken nullable (SQLite doesn't support ALTER COLUMN, so we need to recreate)
            // Check if UserToken is currently NOT NULL
            cmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('Connectors') WHERE name='UserToken' AND \"notnull\"=1";
            count = Convert.ToInt32(await cmd.ExecuteScalarAsync());
            if (count > 0)
            {
                // SQLite doesn't support ALTER COLUMN to change nullability
                // We'll handle this at the application level - the column will remain NOT NULL in DB
                // but EF Core will treat it as nullable based on the model
                // For existing data, we ensure all rows have a value
                cmd.CommandText = "UPDATE Connectors SET UserToken = '' WHERE UserToken IS NULL";
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

static async Task MigrateWorkspaceRepositoriesDependencyLevelAndDependenciesAsync(AppDbContext dbContext)
{
    try
    {
        var conn = dbContext.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
            await conn.OpenAsync();

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('WorkspaceRepositories') WHERE name='DependencyLevel'";
            var hasDependencyLevel = Convert.ToInt32(await cmd.ExecuteScalarAsync()) != 0;
            cmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('WorkspaceRepositories') WHERE name='Sequence'";
            var hasSequence = Convert.ToInt32(await cmd.ExecuteScalarAsync()) != 0;

            if (!hasDependencyLevel)
            {
                cmd.CommandText = "ALTER TABLE WorkspaceRepositories ADD COLUMN DependencyLevel INTEGER";
                await cmd.ExecuteNonQueryAsync();
                if (hasSequence)
                {
                    cmd.CommandText = "UPDATE WorkspaceRepositories SET DependencyLevel = Sequence";
                    await cmd.ExecuteNonQueryAsync();
                    cmd.CommandText = "CREATE TABLE WorkspaceRepositories_new(WorkspaceRepositoryId INTEGER PRIMARY KEY AUTOINCREMENT, WorkspaceId INTEGER NOT NULL, RepositoryId INTEGER NOT NULL, GitVersion TEXT, BranchName TEXT, Projects INTEGER, OutgoingCommits INTEGER, IncomingCommits INTEGER, SyncStatus INTEGER NOT NULL, DependencyLevel INTEGER, Dependencies INTEGER, UnmatchedDeps INTEGER)";
                    await cmd.ExecuteNonQueryAsync();
                    cmd.CommandText = "INSERT INTO WorkspaceRepositories_new SELECT WorkspaceRepositoryId, WorkspaceId, RepositoryId, GitVersion, BranchName, Projects, OutgoingCommits, IncomingCommits, SyncStatus, DependencyLevel, Dependencies, UnmatchedDeps FROM WorkspaceRepositories";
                    await cmd.ExecuteNonQueryAsync();
                    cmd.CommandText = "DROP TABLE WorkspaceRepositories";
                    await cmd.ExecuteNonQueryAsync();
                    cmd.CommandText = "ALTER TABLE WorkspaceRepositories_new RENAME TO WorkspaceRepositories";
                    await cmd.ExecuteNonQueryAsync();
                    cmd.CommandText = "CREATE UNIQUE INDEX IX_WorkspaceRepositories_WorkspaceId_RepositoryId ON WorkspaceRepositories(WorkspaceId, RepositoryId)";
                    await cmd.ExecuteNonQueryAsync();
                }
            }
            else if (hasSequence)
            {
                cmd.CommandText = "CREATE TABLE WorkspaceRepositories_new(WorkspaceRepositoryId INTEGER PRIMARY KEY AUTOINCREMENT, WorkspaceId INTEGER NOT NULL, RepositoryId INTEGER NOT NULL, GitVersion TEXT, BranchName TEXT, Projects INTEGER, OutgoingCommits INTEGER, IncomingCommits INTEGER, SyncStatus INTEGER NOT NULL, DependencyLevel INTEGER, Dependencies INTEGER, UnmatchedDeps INTEGER)";
                await cmd.ExecuteNonQueryAsync();
                cmd.CommandText = "INSERT INTO WorkspaceRepositories_new SELECT WorkspaceRepositoryId, WorkspaceId, RepositoryId, GitVersion, BranchName, Projects, OutgoingCommits, IncomingCommits, SyncStatus, DependencyLevel, Dependencies, UnmatchedDeps FROM WorkspaceRepositories";
                await cmd.ExecuteNonQueryAsync();
                cmd.CommandText = "DROP TABLE WorkspaceRepositories";
                await cmd.ExecuteNonQueryAsync();
                cmd.CommandText = "ALTER TABLE WorkspaceRepositories_new RENAME TO WorkspaceRepositories";
                await cmd.ExecuteNonQueryAsync();
                cmd.CommandText = "CREATE UNIQUE INDEX IX_WorkspaceRepositories_WorkspaceId_RepositoryId ON WorkspaceRepositories(WorkspaceId, RepositoryId)";
                await cmd.ExecuteNonQueryAsync();
            }

            cmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('WorkspaceRepositories') WHERE name='Dependencies'";
            var count = Convert.ToInt32(await cmd.ExecuteScalarAsync());
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

static async Task MigrateRepositoryBranchesAsync(AppDbContext dbContext)
{
    try
    {
        var conn = dbContext.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
            await conn.OpenAsync();

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='RepositoryBranches'";
            var tableExists = Convert.ToInt32(await cmd.ExecuteScalarAsync()) > 0;
            
            if (!tableExists)
            {
                cmd.CommandText = @"
                    CREATE TABLE RepositoryBranches (
                        RepositoryBranchId INTEGER PRIMARY KEY AUTOINCREMENT,
                        WorkspaceRepositoryId INTEGER NOT NULL,
                        BranchName TEXT NOT NULL,
                        IsRemote INTEGER NOT NULL,
                        LastSeenAt TEXT NOT NULL,
                        FOREIGN KEY (WorkspaceRepositoryId) REFERENCES WorkspaceRepositories(WorkspaceRepositoryId) ON DELETE CASCADE,
                        UNIQUE(WorkspaceRepositoryId, BranchName, IsRemote)
                    )";
                await cmd.ExecuteNonQueryAsync();
                
                cmd.CommandText = "CREATE INDEX IX_RepositoryBranches_WorkspaceRepositoryId ON RepositoryBranches(WorkspaceRepositoryId)";
                await cmd.ExecuteNonQueryAsync();
            }
        }
    }
    catch
    {
        // Migration may already be applied or table doesn't exist yet
    }
}

static async Task MigrateRepositoryBranchesIsDefaultAsync(AppDbContext dbContext)
{
    try
    {
        var conn = dbContext.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
            await conn.OpenAsync();

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('RepositoryBranches') WHERE name='IsDefault'";
            var count = Convert.ToInt32(await cmd.ExecuteScalarAsync());
            if (count == 0)
            {
                cmd.CommandText = "ALTER TABLE RepositoryBranches ADD COLUMN IsDefault INTEGER NOT NULL DEFAULT 0";
                await cmd.ExecuteNonQueryAsync();
            }
        }
    }
    catch
    {
        // Migration may already be applied or table doesn't exist yet
    }
}

static async Task MigrateWorkspaceFilesAsync(AppDbContext dbContext)
{
    try
    {
        var conn = dbContext.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
            await conn.OpenAsync();

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='WorkspaceFiles'";
            var tableExists = Convert.ToInt32(await cmd.ExecuteScalarAsync()) > 0;

            if (!tableExists)
            {
                cmd.CommandText = @"
                    CREATE TABLE WorkspaceFiles (
                        FileId INTEGER PRIMARY KEY AUTOINCREMENT,
                        WorkspaceId INTEGER NOT NULL,
                        RepositoryId INTEGER NOT NULL,
                        FileName TEXT NOT NULL,
                        FilePath TEXT NOT NULL,
                        FOREIGN KEY (WorkspaceId) REFERENCES Workspaces(WorkspaceId) ON DELETE CASCADE,
                        FOREIGN KEY (RepositoryId) REFERENCES Repositories(RepositoryId) ON DELETE CASCADE,
                        UNIQUE(WorkspaceId, RepositoryId, FilePath)
                    )";
                await cmd.ExecuteNonQueryAsync();

                cmd.CommandText = "CREATE INDEX IX_WorkspaceFiles_WorkspaceId ON WorkspaceFiles(WorkspaceId)";
                await cmd.ExecuteNonQueryAsync();
            }
        }
    }
    catch
    {
        // Migration may already be applied or table doesn't exist yet
    }
}

static async Task MigrateWorkspaceFileVersionConfigsAsync(AppDbContext dbContext)
{
    try
    {
        var conn = dbContext.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
            await conn.OpenAsync();

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='WorkspaceFileVersionConfigs'";
            var tableExists = Convert.ToInt32(await cmd.ExecuteScalarAsync()) > 0;

            if (!tableExists)
            {
                cmd.CommandText = @"
                    CREATE TABLE WorkspaceFileVersionConfigs (
                        ConfigId INTEGER PRIMARY KEY AUTOINCREMENT,
                        FileId INTEGER NOT NULL UNIQUE,
                        VersionPattern TEXT NOT NULL,
                        FOREIGN KEY (FileId) REFERENCES WorkspaceFiles(FileId) ON DELETE CASCADE
                    )";
                await cmd.ExecuteNonQueryAsync();

                cmd.CommandText = "CREATE INDEX IX_WorkspaceFileVersionConfigs_FileId ON WorkspaceFileVersionConfigs(FileId)";
                await cmd.ExecuteNonQueryAsync();
            }
        }
    }
    catch
    {
        // Migration may already be applied or table doesn't exist yet
    }
}

static async Task MigrateSettingsAsync(AppDbContext dbContext)
{
    try
    {
        var conn = dbContext.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
            await conn.OpenAsync();

        await using (var cmd = conn.CreateCommand())
        {
            // Rename old AppSettings table to Settings if needed
            cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='Settings'";
            var settingsExists = Convert.ToInt32(await cmd.ExecuteScalarAsync()) > 0;

            if (!settingsExists)
            {
                cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='AppSettings'";
                var oldTableExists = Convert.ToInt32(await cmd.ExecuteScalarAsync()) > 0;

                if (oldTableExists)
                {
                    cmd.CommandText = "ALTER TABLE AppSettings RENAME TO Settings";
                    await cmd.ExecuteNonQueryAsync();
                }
                else
                {
                    cmd.CommandText = @"
                        CREATE TABLE Settings (
                            SettingId INTEGER PRIMARY KEY AUTOINCREMENT,
                            Key TEXT NOT NULL,
                            Value TEXT,
                            UNIQUE(Key)
                        )";
                    await cmd.ExecuteNonQueryAsync();
                }
            }

            // Rename AppSettingId column to SettingId if it still exists
            cmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('Settings') WHERE name='AppSettingId'";
            var oldColumnExists = Convert.ToInt32(await cmd.ExecuteScalarAsync()) > 0;
            if (oldColumnExists)
            {
                cmd.CommandText = "ALTER TABLE Settings RENAME COLUMN AppSettingId TO SettingId";
                await cmd.ExecuteNonQueryAsync();
            }
        }
    }
    catch
    {
        // Migration may already be applied or table doesn't exist yet
    }
}

static async Task MigrateWorkspaceRootPathAsync(AppDbContext dbContext)
{
    try
    {
        var conn = dbContext.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
            await conn.OpenAsync();

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('Workspaces') WHERE name='RootPath'";
            var count = Convert.ToInt32(await cmd.ExecuteScalarAsync());
            if (count == 0)
            {
                cmd.CommandText = "ALTER TABLE Workspaces ADD COLUMN RootPath TEXT";
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
app.MapHub<AgentHub>("/hub/agent");

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
