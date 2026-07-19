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
using GrayMoon.App.Services.GitChanges;
using GrayMoon.App.Services.Queries;
using GrayMoon.App.Services.Security;
using GrayMoon.Common;
using GrayMoon.Common.Git;
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

    // Desktop mode: launched with --desktop <pipe-name> by GrayMoon.Desktop.exe
    var desktopPipeIndex = Array.IndexOf(args, "--desktop");
    var isDesktopMode = desktopPipeIndex >= 0 && desktopPipeIndex + 1 < args.Length;
    var desktopPipeName = isDesktopMode ? args[desktopPipeIndex + 1] : null;

    if (isDesktopMode && desktopPipeName is not null)
    {
        builder.WebHost.UseDesktopMode(desktopPipeName);
        builder.Services.AddHostedService<DesktopStartupService>();
    }

    builder.Logging.ClearProviders();
    builder.Host.UseSerilog((context, services, configuration) => configuration
        .ReadFrom.Configuration(context.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext());
    builder.Configuration.AddEnvironmentVariables();

    builder.Services.Configure<WorkspaceOptions>(builder.Configuration.GetSection("Workspace"));
    builder.Services.Configure<GitChangesOptions>(builder.Configuration.GetSection("GitChanges"));

    // Add services to the container.
    builder.Services.AddRazorComponents()
        .AddInteractiveServerComponents();

    // Default SignalR incoming message limit is 32KB. SyncRepository ResponseCommand carries branch lists
    // plus full .csproj/package graphs; larger repos exceed that and the server closes the connection,
    // which surfaces on the agent as HubException during InvokeAsync (not a git failure).
    // SyncCommand no longer awaits agent responses inline - it enqueues to AgentSyncNotificationQueue and
    // returns immediately - so the default MaximumParallelInvocationsPerClient = 1 is sufficient.
    builder.Services.AddSignalR(options =>
    {
        options.MaximumReceiveMessageSize = 10 * 1024 * 1024;
    });

    // Database (SQLite) for persisted data - stored in db/ for easy container volume mounting.
    // WAL + busy_timeout so a large workspace's reads/writes do not block other circuits as hard.
    var connectionString = builder.Configuration.GetConnectionString("DefaultConnection") ?? "Data Source=db/graymoon.db";
    if (!connectionString.Contains("Cache=", StringComparison.OrdinalIgnoreCase))
        connectionString += connectionString.EndsWith(';') ? "Cache=Shared;" : ";Cache=Shared;";
    // AddDbContext + AddDbContextFactory together fails on EF Core 10 (scoped
    // IDbContextOptionsConfiguration resolved from the root provider). Register the
    // factory only, then supply scoped AppDbContext instances from it.
    builder.Services.AddDbContextFactory<AppDbContext>(options => options.UseSqlite(connectionString));
    builder.Services.AddScoped(sp => sp.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContext());
    builder.Services.AddScoped<ConnectorRepository>();
    builder.Services.AddScoped<GitHubRepositoryRepository>();
    builder.Services.AddScoped<WorkspaceProjectRepository>();
    builder.Services.AddScoped<WorkspaceFileRepository>();
    builder.Services.AddScoped<WorkspaceFileVersionConfigRepository>();
    builder.Services.AddScoped<WorkspaceRepositoryCustomDependencyRepository>();
    builder.Services.AddScoped<WorkspaceRepository>();
    builder.Services.AddScoped<AppSettingRepository>();
    builder.Services.AddScoped<NavbarCollapseService>();
    builder.Services.AddSingleton<AgentConnectionTracker>();
    builder.Services.AddSingleton<AgentQueueStateService>();
    builder.Services.AddSingleton<AgentCommandCancelSender>();
    builder.Services.AddSingleton<OverlayCommandTerminalService>();
    builder.Services.AddSingleton<IToastService, ToastService>();
    builder.Services.AddSingleton<MatrixOverlayPreferenceService>();
    builder.Services.AddSingleton<CommandTerminalOverlayPreferenceService>();
    builder.Services.AddSingleton<LoadingOverlayUiSettingsService>();
    builder.Services.AddSingleton<IGitHubRateLimitTracker, GitHubRateLimitTracker>();
    builder.Services.AddScoped<SyncCommandHandler>();
    builder.Services.AddScoped<IAgentBridge, AgentBridge>();
    builder.Services.AddScoped<WorkspaceService>();
    builder.Services.AddScoped<WorkspaceGitService>();
    builder.Services.AddScoped<ConnectorHealthService>();
    builder.Services.AddScoped<GitHubRepositoryService>();
    builder.Services.AddScoped<IRepositoryListQueryService, RepositoryListQueryService>();
    builder.Services.AddScoped<IWorkspaceProjectListQueryService, WorkspaceProjectListQueryService>();
    builder.Services.AddScoped<IWorkspaceRepositoryLinkListQueryService, WorkspaceRepositoryLinkListQueryService>();
    builder.Services.AddMemoryCache();
    builder.Services.AddScoped<GitHubActionsService>();
    builder.Services.AddScoped<GhaWorkflowLiveFeedService>();
    builder.Services.AddScoped<GitHubPullRequestService>();
    builder.Services.AddScoped<WorkspacePullRequestRepository>();
    builder.Services.AddScoped<WorkspacePullRequestService>();
    builder.Services.AddScoped<WorkspaceActionRepository>();
    builder.Services.AddScoped<WorkspaceActionService>();
    builder.Services.AddScoped<PackageRegistrySyncService>();
    builder.Services.AddScoped<IWorkspaceFileSearchService, WorkspaceFileSearchService>();
    builder.Services.AddScoped<WorkspaceFileVersionService>();
    builder.Services.AddScoped<WorkspaceCommitSyncHandler>();
    builder.Services.AddScoped<WorkspaceBranchUpdateHandler>();
    builder.Services.AddScoped<WorkspaceSyncHandler>();
    builder.Services.AddScoped<DependencyUpdateOrchestrator>();
    builder.Services.AddScoped<WorkspaceUpdateHandler>();
    builder.Services.AddScoped<PushOrchestrator>();
    builder.Services.AddScoped<WorkspacePushService>();
    builder.Services.AddScoped<WorkspacePushHandler>();
    builder.Services.AddScoped<WorkspaceUndoPushHandler>();
    builder.Services.AddScoped<WorkspaceDependencyService>();
    builder.Services.AddScoped<WorkspacePendingActionsService>();
    builder.Services.AddScoped<WorkspaceBranchHandler>();
    builder.Services.AddScoped<NewFeatureOrchestrator>();
    builder.Services.AddScoped<IBackgroundJobService, BackgroundJobService>();
    builder.Services.AddScoped<IWorkspacePageService, WorkspacePageService>();
    builder.Services.AddScoped<IWorkspaceTopBarService, WorkspaceTopBarService>();

    builder.Services.AddScoped<IWorkspaceGitChangesReadService, WorkspaceGitChangesReadService>();
    builder.Services.AddScoped<IGitChangesAgentClient, GitChangesAgentClient>();
    builder.Services.AddScoped<GitChangesSnapshotPushHandler>();
    builder.Services.AddScoped<WorkspaceGitChangesSelectionMemory>();
    builder.Services.AddSingleton<IWorkspaceGitChangesActivityTracker, WorkspaceGitChangesActivityTracker>();
    builder.Services.AddSingleton<IGitChangesWorkspaceScanner, GitChangesWorkspaceScanner>();

    builder.Services.AddSingleton<ICommandLineService, CommandLineService>();
    builder.Services.AddSingleton<IScopedServiceExecutor, ScopedServiceExecutor>();

    // Token protection
    builder.Services.AddSingleton<ITokenEncryptionKeyProvider, TokenEncryptionKeyProvider>();
    builder.Services.AddSingleton<ITokenProtector, AesGcmTokenProtector>();

    // Background services
    builder.Services.AddSingleton<SyncBackgroundService>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<SyncBackgroundService>());
    builder.Services.AddSingleton<AgentSyncNotificationQueue>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<AgentSyncNotificationQueue>());
    builder.Services.AddHostedService<TokenHealthBackgroundService>();
    builder.Services.AddSingleton<WorkspaceGitChangesWriteQueue>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<WorkspaceGitChangesWriteQueue>());
    builder.Services.AddHostedService<GitChangesMonitoringBackgroundService>();

    // Connector services
    builder.Services.AddTransient<GitHubOverlayLoggingHandler>();
    builder.Services.AddHttpClient<GitHubService>()
        .AddHttpMessageHandler<GitHubOverlayLoggingHandler>();
    builder.Services.AddHttpClient<NuGetService>();
    builder.Services.AddScoped<ConnectorServiceFactory>();
    builder.Services.AddScoped<IPullRequestService, PullRequestService>();

    // Persist Data Protection keys to db volume so antiforgery and other protected data survive container restarts
    var keyRingDir = Path.Combine(Path.GetDirectoryName(GetDatabasePath(connectionString) ?? "db") ?? "db", "DataProtection-Keys");
    builder.Services.AddDataProtection()
        .SetApplicationName("GrayMoon")
        .PersistKeysToFileSystem(new DirectoryInfo(keyRingDir));

    var app = builder.Build();
    AgentResponseDelivery.SetCancelNotifier(app.Services.GetRequiredService<AgentCommandCancelSender>().NotifyCancel);
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
        await Migrations.RunAllAsync(dbContext);

        await dbContext.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
        await dbContext.Database.ExecuteSqlRawAsync("PRAGMA busy_timeout=5000;");
        await dbContext.Database.ExecuteSqlRawAsync("PRAGMA synchronous=NORMAL;");
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
    // Also skip in desktop mode — loopback HTTP only, no HTTPS needed.
    if (!isDesktopMode && (app.Configuration["ASPNETCORE_URLS"] ?? "").Contains("https", StringComparison.OrdinalIgnoreCase))
    {
        app.UseHttpsRedirection();
    }

    app.UseStaticFiles();
    app.UseAntiforgery();

    app.MapStaticAssets();

    app.MapApiEndpoints();
    app.MapHub<WorkspaceSyncHub>("/hubs/workspace-sync");
    app.MapHub<AgentHub>("/hub/agent");

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
catch (Exception ex)
{
    Log.Fatal(ex, "Application terminated unexpectedly");
    return 1;
}
finally
{
    await Log.CloseAndFlushAsync();
}
