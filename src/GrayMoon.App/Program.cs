using System.Reflection;
using GrayMoon.App;
using GrayMoon.App.Api;
using GrayMoon.App.Components;
using GrayMoon.App.Data;
using GrayMoon.App.Desktop;
using GrayMoon.App.Hubs;
using GrayMoon.App.Models;
using GrayMoon.App.Repositories;
using GrayMoon.App.Services;
using GrayMoon.App.Services.Ai;
using GrayMoon.App.Services.Ci;
using GrayMoon.App.Services.Worker;
using GrayMoon.App.Services.Connectors;
using GrayMoon.App.Services.Git;
using GrayMoon.App.Services.GitChanges;
using GrayMoon.App.Services.GitHub;
using GrayMoon.App.Services.Jobs;
using GrayMoon.App.Services.Orchestration;
using GrayMoon.App.Services.Queries;
using GrayMoon.App.Services.Security;
using GrayMoon.App.Services.Ui;
using GrayMoon.App.Services.Features;
using GrayMoon.App.Services.Workspaces;
using GrayMoon.Application.Ai;
using GrayMoon.Application.Features;
using GrayMoon.Application.Workspaces;
using GrayMoon.Common;
using GrayMoon.Common.Git;
using GrayMoon.Common.Manifest;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Serilog;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    // In Development the container is checked when it is built: a scoped service injected into a singleton
    // (for example the per-circuit IToastService into a hosted service) stops startup instead of sharing
    // one instance between every browser tab.
    builder.Host.UseDefaultServiceProvider((context, options) =>
    {
        var isDevelopment = context.HostingEnvironment.IsDevelopment();
        options.ValidateScopes = isDevelopment;
        options.ValidateOnBuild = isDevelopment;
    });

    // Desktop mode: launched with --desktop <pipe-name> by GrayMoon.Desktop.exe
    var desktopPipeIndex = Array.IndexOf(args, "--desktop");
    var isDesktopMode = desktopPipeIndex >= 0 && desktopPipeIndex + 1 < args.Length;
    var desktopPipeName = isDesktopMode ? args[desktopPipeIndex + 1] : null;

    if (isDesktopMode && desktopPipeName is not null)
    {
        builder.WebHost.UseDesktopMode(desktopPipeName);
        builder.Services.AddHostedService<DesktopStartupService>();
    }

    // GrayMoon.Desktop launches this process with GRAYMOON_-prefixed env vars (e.g.
    // GRAYMOON_Desktop__LogDirectory) so it does not collide with ASPNETCORE_/other host env vars.
    builder.Configuration.AddEnvironmentVariables(prefix: "GRAYMOON_");

    var logDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "GrayMoon",
        "Logs");
    Directory.CreateDirectory(logDirectory);

    builder.Logging.ClearProviders();
    builder.Host.UseSerilog((context, services, configuration) => configuration
        .ReadFrom.Configuration(context.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext()
        .WriteTo.File(
            path: Path.Combine(logDirectory, "graymoon-app-.log"),
            rollingInterval: RollingInterval.Day,
            retainedFileCountLimit: 14,
            outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj} req={GrayMoonRequestId} op={GrayMoonOperationId}{NewLine}{Exception}"));

    // Database (SQLite) for persisted data - stored in db/ for easy container volume mounting.
    // WAL + busy_timeout so a large workspace's reads/writes do not block other circuits as hard.
    var connectionString = builder.Configuration.GetConnectionString("DefaultConnection") ?? "Data Source=db/graymoon.db";
    connectionString = EnsureConnectionSetting(connectionString, "Cache=", "Cache=Shared");
    connectionString = EnsureConnectionSetting(connectionString, "Default Timeout=", "Default Timeout=5");
    connectionString = EnsureConnectionSetting(connectionString, "Foreign Keys=", "Foreign Keys=True");
    builder.Services.AddGrayMoonServices(builder.Configuration, connectionString);

    var app = builder.Build();
    WorkerResponseDelivery.SetCancelNotifier(app.Services.GetRequiredService<WorkerCommandCancelSender>().NotifyCancel);
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

        // Initialize static helper to use configured token protector
        ConnectorHelpers.InitializeTokenProtector(tokenProtector);

        dbContext.Database.EnsureCreated();
        await Migrations.RunAllAsync(dbContext, app.Logger);

        var keyProvider = services.GetRequiredService<ITokenEncryptionKeyProvider>();
        await TokenReencryptionService.ReencryptLegacyTokensAsync(dbContext, tokenProtector, keyProvider, app.Logger);

        await services.GetRequiredService<WorkerSecretService>().InitializeAsync();

        await dbContext.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
        await dbContext.Database.ExecuteSqlRawAsync("PRAGMA busy_timeout=5000;");
        await dbContext.Database.ExecuteSqlRawAsync("PRAGMA synchronous=NORMAL;");

        // Load the persisted top bar visibility before any Blazor circuit or Desktop hub
        // connection can ask for it - see DesktopTopBarState.
        var appSettingRepository = services.GetRequiredService<AppSettingRepository>();
        var topBarVisible = await appSettingRepository.GetBoolAsync(AppSettingRepository.TopBarShowKey, defaultValue: true);
        services.GetRequiredService<DesktopTopBarState>().LoadSilently(topBarVisible);
    }

    static string? GetDatabasePath(string connectionString) => DatabasePathResolver.GetDatabasePath(connectionString);

    static string EnsureConnectionSetting(string connectionString, string keyPrefix, string assignment)
    {
        if (connectionString.Contains(keyPrefix, StringComparison.OrdinalIgnoreCase))
            return connectionString;
        if (!connectionString.EndsWith(';'))
            connectionString += ';';
        return connectionString + assignment + ';';
    }

    // Configure the HTTP request pipeline.
    if (!app.Environment.IsDevelopment())
    {
        app.UseExceptionHandler("/Error", createScopeForErrors: true);
        // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
        app.UseHsts();
    }

    // Only redirect to HTTPS when URLs include HTTPS (skip in container when only HTTP is used)
    // Also skip in desktop mode - loopback HTTP only, no HTTPS needed.
    if (!isDesktopMode && (app.Configuration["ASPNETCORE_URLS"] ?? "").Contains("https", StringComparison.OrdinalIgnoreCase))
    {
        app.UseHttpsRedirection();
    }

    // F3: cross-site/rebinding check, before routing reaches any API endpoint or hub.
    app.UseMiddleware<RequestSecurityMiddleware>();
    // F2: Worker secret on /hub/worker and /repos/{id}/connector.
    app.UseMiddleware<WorkerSecretMiddleware>();

    app.UseStaticFiles();
    app.UseAntiforgery();

    app.MapStaticAssets();

    app.MapApiEndpoints();
    app.MapHub<WorkspaceSyncHub>("/hubs/workspace-sync");
    app.MapHub<WorkerHub>(WorkerHubRoutes.Path);
    // Workers installed before the Agent -> Worker rename still connect to the old path.
    app.MapHub<WorkerHub>(WorkerHubRoutes.LegacyPath);

    // Desktop-mode-only endpoints
    if (isDesktopMode)
    {
        app.MapHub<DesktopNotificationHub>("/hubs/desktop");
    }

    app.MapGet("/health", () => Results.Ok());

    app.MapRazorComponents<App>()
        .AddInteractiveServerRenderMode();

    await app.RunAsync();
    return 0;
}
catch (DatabaseMigrationException dbEx)
{
    Log.Fatal(dbEx, "Database migration failed at startup: {Message}", dbEx.Message);
    return 1;
}
catch (Exception ex)
{
    Log.Fatal(ex, "Application terminated unexpectedly");
    return IsAddressInUseException(ex) ? DesktopHostingExtensions.AddressInUseExitCode : 1;
}
finally
{
    await Log.CloseAndFlushAsync();
}

static bool IsAddressInUseException(Exception? ex)
{
    while (ex is not null)
    {
        if (ex is Microsoft.AspNetCore.Connections.AddressInUseException) return true;
        ex = ex.InnerException;
    }
    return false;
}
