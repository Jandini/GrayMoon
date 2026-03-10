using System.Reflection;
using GrayMoon.App;
using GrayMoon.App.Api;
using GrayMoon.App.Components;
using GrayMoon.App.Data;
using GrayMoon.App.Hubs;
using GrayMoon.App.Models;
using GrayMoon.App.Repositories;
using GrayMoon.App.Services;
using GrayMoon.App.Services.Security;
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
builder.Services.AddScoped<GitHubPullRequestService>();
builder.Services.AddScoped<WorkspacePullRequestRepository>();
builder.Services.AddScoped<WorkspacePullRequestService>();
builder.Services.AddScoped<PackageRegistrySyncService>();
builder.Services.AddScoped<IWorkspaceFileSearchService, WorkspaceFileSearchService>();
    builder.Services.AddScoped<WorkspaceFileVersionService>();

// Token protection and connector health
builder.Services.AddSingleton<ITokenEncryptionKeyProvider, TokenEncryptionKeyProvider>();
builder.Services.AddSingleton<ITokenProtector, AesGcmTokenProtector>();
builder.Services.AddSingleton<ConnectorHealthTracker>();

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
//var version = Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
    var version = typeof(Program).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
        .InformationalVersion
        ?? "unknown";


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
    var services = scope.ServiceProvider;
    var dbContext = services.GetRequiredService<AppDbContext>();
    var tokenProtector = services.GetRequiredService<ITokenProtector>();
    var connectorHealth = services.GetRequiredService<ConnectorHealthTracker>();
    var logger = services.GetRequiredService<ILogger<Program>>();

    // Initialize static helper to use configured token protector
    ConnectorHelpers.InitializeTokenProtector(tokenProtector);

    dbContext.Database.EnsureCreated();
    await Migrations.RunAllAsync(dbContext);

    // Verify that all stored connector tokens are decryptable; flag health if not.
    try
    {
        var connectors = await dbContext.Connectors
            .AsNoTracking()
            .ToListAsync();

        var hadError = false;
        foreach (var connector in connectors)
        {
            if (string.IsNullOrWhiteSpace(connector.UserToken))
                continue;

            try
            {
                _ = tokenProtector.Unprotect(connector.UserToken);
            }
            catch (Exception ex)
            {
                hadError = true;
                logger.LogWarning(ex, "Failed to decrypt connector token. ConnectorId={ConnectorId}, Name={ConnectorName}", connector.ConnectorId, connector.ConnectorName);
            }
        }

        connectorHealth.HasTokenDecryptionErrors = hadError;
        if (hadError)
        {
            logger.LogWarning("One or more connector tokens could not be decrypted. Check TokenKey configuration before using connectors.");
        }
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "Connector token decryption health check failed.");
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
