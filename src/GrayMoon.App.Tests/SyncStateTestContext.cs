using GrayMoon.Abstractions.Worker;
using GrayMoon.Abstractions.Workspaces;
using GrayMoon.App.Data;
using GrayMoon.App.Hubs;
using GrayMoon.App.Models;
using GrayMoon.App.Repositories;
using GrayMoon.App.Services;
using GrayMoon.App.Services.Worker;
using GrayMoon.App.Services.Features;
using GrayMoon.App.Services.GitChanges;
using GrayMoon.App.Services.Jobs;
using GrayMoon.App.Services.Queries;
using GrayMoon.App.Services.Workspaces;
using GrayMoon.Application.Features;
using GrayMoon.Application.Workspaces;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace GrayMoon.App.Tests;

/// <summary>
/// Real DI container over an in-memory SQLite database, used by the write-side tests
/// (sync command handling, return-to-default persistence, PR refresh). The only substituted
/// dependencies are the worker transport and the SignalR hub, so the services under test run
/// their production code paths against a real EF Core model.
/// </summary>
public sealed class SyncStateTestContext : IAsyncDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _provider;

    public FakeWorkerBridge WorkerBridge { get; }
    public FakeHubContext<WorkspaceSyncHub> HubContext { get; }
    public int WorkspaceId { get; private set; }
    public int RepositoryId { get; private set; }
    public int WorkspaceRepositoryId { get; private set; }

    private SyncStateTestContext(SqliteConnection connection, ServiceProvider provider, FakeWorkerBridge workerBridge, FakeHubContext<WorkspaceSyncHub> hubContext)
    {
        _connection = connection;
        _provider = provider;
        WorkerBridge = workerBridge;
        HubContext = hubContext;
    }

    public static async Task<SyncStateTestContext> CreateAsync(
        string? userToken = null,
        Action<IServiceCollection>? configureServices = null,
        Action<DbContextOptionsBuilder>? configureDb = null)
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var workerBridge = new FakeWorkerBridge();
        var hubContext = new FakeHubContext<WorkspaceSyncHub>();

        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.Configure<WorkspaceOptions>(o => o.MaxParallelOperations = 4);
        services.AddSingleton<IGitHubRateLimitTracker, GitHubRateLimitTracker>();
        services.AddSingleton<IGitHubETagCache, GitHubETagCache>();
        services.AddSingleton<IGitHubApiUsageRecorder, FakeGitHubApiUsageRecorder>();
        services.AddSingleton(new HttpClient());
        services.AddSingleton<IWorkerBridge>(workerBridge);
        services.AddSingleton<IHubContext<WorkspaceSyncHub>>(hubContext);

        void ConfigureDb(DbContextOptionsBuilder o)
        {
            o.UseSqlite(connection);
            configureDb?.Invoke(o);
        }

        services.AddDbContext<AppDbContext>(ConfigureDb, ServiceLifetime.Scoped);
        services.AddDbContextFactory<AppDbContext>(ConfigureDb, ServiceLifetime.Singleton);

        services.AddSingleton<IWorkspaceGitChangesNotifier, WorkspaceGitChangesNotifier>();
        services.AddSingleton<IWorkspaceGitChangesMonitoringPause, WorkspaceGitChangesMonitoringPause>();
        services.AddScoped<AppSettingRepository>();
        services.AddScoped<ConnectorRepository>();
        services.AddScoped<GitHubRepositoryRepository>();
        services.AddScoped<WorkspaceRepository>();
        services.AddScoped<WorkspaceProjectRepository>();
        services.AddScoped<WorkspacePullRequestRepository>();
        services.AddScoped<WorkspaceFileVersionConfigRepository>();
        services.AddScoped<WorkspaceRepositoryCustomDependencyRepository>();

        services.AddScoped<WorkspaceService>();
        services.AddScoped<IWorkspaceCapabilitiesResolver, WorkspaceCapabilitiesResolver>();
        services.AddScoped<IWorkspaceFeatureContextResolver, WorkspaceFeatureContextResolver>();
        services.AddScoped<IWorkspaceContextPathResolver, WorkspaceContextPathResolver>();
        services.AddScoped<IWorkspaceSelectedFeatureContextService, WorkspaceSelectedFeatureContextService>();
        services.AddScoped<IWorkspaceHookContextAttributor, WorkspaceHookContextAttributor>();
        services.AddScoped<IWorkspaceFeatureOperations, WorkspaceFeatureOperations>();
        services.AddScoped<IWorkspaceExternalWorktreeOperations, WorkspaceExternalWorktreeOperations>();
        services.AddSingleton<WorkerConnectionTracker>();
        services.AddSingleton<FeatureFinalizationCoordinator>();
        services.AddSingleton<IWorkspaceFeatureReconciler, WorkspaceFeatureReconciler>();
        services.AddSingleton<IWorkspaceOperationRunner, WorkspaceOperationRunner>();
        services.AddSingleton<IWorkspaceOperationLock>(sp => (IWorkspaceOperationLock)sp.GetRequiredService<IWorkspaceOperationRunner>());
        services.AddScoped<GitHubService>();
        services.AddScoped<GitHubPullRequestService>();
        services.AddScoped<GitHubPullRequestMergeService>();
        services.AddScoped<WorkspacePullRequestService>();
        services.AddScoped<RepositoryBranchWriter>();
        services.AddScoped<WorkspaceRepositoryStateWriter>();
        services.AddScoped<WorkspaceStateRecomputeScope>();
        services.AddScoped<WorkspaceDependencyService>();
        services.AddScoped<WorkspaceFileVersionService>();
        services.AddScoped<WorkspaceGitService>();
        services.AddScoped<ConnectorHealthService>();
        services.AddScoped<WorkspaceCommitSyncHandler>();
        services.AddScoped<IWorkspaceRepositoryLinkListQueryService, WorkspaceRepositoryLinkListQueryService>();
        services.AddScoped<WorkspaceRepositoryLinkListQueryService>();
        services.AddScoped<WorkspaceSyncHandler>();
        services.AddScoped<SyncCommandHandler>();
        services.AddScoped<WorkspaceBranchUpdateHandler>();
        services.AddScoped<IFeatureBranchGuard, FeatureBranchGuard>();
        services.AddScoped<IWorkspaceBranchOperations, WorkspaceBranchOperations>();

        // Last registration wins for GetRequiredService; tests can replace path resolution, etc.
        configureServices?.Invoke(services);

        var provider = services.BuildServiceProvider();

        var ctx = new SyncStateTestContext(connection, provider, workerBridge, hubContext);
        await ctx.SeedAsync(userToken);
        return ctx;
    }

    private async Task SeedAsync(string? userToken)
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.EnsureCreatedAsync();

        var connector = new Connector
        {
            ConnectorName = "github-prod",
            ConnectorType = ConnectorType.GitHub,
            ApiBaseUrl = "https://api.github.com",
            IsActive = true,
            IsHealthy = true,
            UserToken = userToken,
        };
        db.Connectors.Add(connector);
        await db.SaveChangesAsync();

        var repository = new Repository
        {
            ConnectorId = connector.ConnectorId,
            RepositoryName = "graymoon-api",
            OrgName = "acme",
            Visibility = "Public",
            CloneUrl = "https://github.com/acme/graymoon-api.git",
        };
        db.Repositories.Add(repository);
        await db.SaveChangesAsync();

        var workspace = new Workspace { Name = "test-ws", RootPath = @"C:\gm-test-root" };
        db.Workspaces.Add(workspace);
        db.Settings.Add(new Setting
        {
            Key = AppSettingRepository.FeatureStorageRootPathKey,
            Value = @"C:\Users\test\.graymoon",
        });
        await db.SaveChangesAsync();

        var link = new WorkspaceRepositoryLink
        {
            WorkspaceId = workspace.WorkspaceId,
            RepositoryId = repository.RepositoryId,
            GitVersion = "1.0.0",
            BranchName = "feature/x",
            DefaultBranchName = "main",
            OutgoingCommits = 3,
            IncomingCommits = 2,
            DefaultBranchAheadCommits = 4,
            DefaultBranchBehindCommits = 5,
            BranchHasUpstream = true,
            SyncStatus = RepoSyncStatus.InSync,
            RepositoryType = ProjectType.Library,
        };
        db.WorkspaceRepositories.Add(link);
        await db.SaveChangesAsync();

        await Migrations.MigrateWorkspaceFeatureContextSchemaAsync(db);

        WorkspaceId = workspace.WorkspaceId;
        RepositoryId = repository.RepositoryId;
        WorkspaceRepositoryId = link.WorkspaceRepositoryId;
    }

    public AsyncServiceScope CreateScope() => _provider.CreateAsyncScope();

    public T Resolve<T>() where T : notnull => _provider.GetRequiredService<T>();

    /// <summary>
    /// The seeded workspace carries the model defaults (Basic, no versioning, no CI), which persist no
    /// project state. Tests of .NET project and dependency behaviour switch to the migrated .NET triple.
    /// </summary>
    public async Task UseDotNetDependencyProfileAsync()
    {
        var factory = _provider.GetRequiredService<IDbContextFactory<AppDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        var workspace = await db.Workspaces.FirstAsync(w => w.WorkspaceId == WorkspaceId);
        workspace.Type = WorkspaceType.DotNetDependency;
        workspace.VersioningMode = WorkspaceVersioningMode.GitVersion;
        workspace.CiProvider = WorkspaceCiProvider.GitHubActions;
        await db.SaveChangesAsync();
    }

    /// <summary>Reads the link fresh from its own context so tests never assert against a tracked instance the service under test still holds.</summary>
    public async Task<WorkspaceRepositoryLink> ReadLinkAsync()
    {
        var factory = _provider.GetRequiredService<IDbContextFactory<AppDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        return await db.WorkspaceRepositories
            .AsNoTracking()
            .FirstAsync(wr => wr.WorkspaceId == WorkspaceId && wr.RepositoryId == RepositoryId);
    }

    public async Task<List<RepositoryBranch>> ReadBranchesAsync()
    {
        var factory = _provider.GetRequiredService<IDbContextFactory<AppDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        return await db.RepositoryBranches
            .AsNoTracking()
            .Where(rb => rb.WorkspaceRepositoryId == WorkspaceRepositoryId)
            .ToListAsync();
    }

    public async Task<List<WorkspaceProject>> ReadProjectsAsync()
    {
        var factory = _provider.GetRequiredService<IDbContextFactory<AppDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        return await db.WorkspaceProjects
            .AsNoTracking()
            .Where(p => p.WorkspaceId == WorkspaceId && p.RepositoryId == RepositoryId)
            .ToListAsync();
    }

    public async Task<List<ProjectDependency>> ReadDependenciesAsync()
    {
        var factory = _provider.GetRequiredService<IDbContextFactory<AppDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        return await db.ProjectDependencies
            .AsNoTracking()
            .Include(d => d.DependentProject)
            .Include(d => d.ReferencedProject)
            .Where(d => d.DependentProject != null && d.DependentProject.WorkspaceId == WorkspaceId)
            .ToListAsync();
    }

    public async Task<WorkspaceRepositoryPullRequest?> ReadPullRequestAsync()
    {
        var factory = _provider.GetRequiredService<IDbContextFactory<AppDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        return await db.Set<WorkspaceRepositoryPullRequest>()
            .AsNoTracking()
            .FirstOrDefaultAsync(pr => pr.WorkspaceRepositoryId == WorkspaceRepositoryId);
    }

    public async Task MutateLinkAsync(Action<WorkspaceRepositoryLink> mutate)
    {
        var factory = _provider.GetRequiredService<IDbContextFactory<AppDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        var link = await db.WorkspaceRepositories
            .FirstAsync(wr => wr.WorkspaceId == WorkspaceId && wr.RepositoryId == RepositoryId);
        mutate(link);
        await db.SaveChangesAsync();
    }

    public async Task<WorkspaceFeatureContextId> GetSpecialContextIdAsync()
    {
        await using var scope = CreateScope();
        var resolver = scope.ServiceProvider.GetRequiredService<IWorkspaceFeatureContextResolver>();
        return await resolver.GetOrCreateSpecialWorkspaceContextIdAsync(WorkspaceId);
    }

    public IReadOnlyList<(string Method, object?[] Args)> Broadcasts => HubContext.ClientsImpl.AllProxy.Sent;

    public async ValueTask DisposeAsync()
    {
        await _provider.DisposeAsync();
        await _connection.DisposeAsync();
    }
}

/// <summary>Worker transport stub. Tests register a canned response per command name; unregistered commands fail like a disconnected worker would.</summary>
public sealed class FakeWorkerBridge : IWorkerBridge
{
    private readonly Dictionary<string, Func<object, WorkerCommandResponse>> _handlers = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _callsLock = new();

    public bool IsWorkerConnected { get; set; } = true;
    public List<(string Command, object Args)> Calls { get; } = [];

    public void Respond(string command, object? data, bool success = true, string? error = null)
        => _handlers[command] = _ => new WorkerCommandResponse(success, data, error);

    public void Respond(string command, Func<object, WorkerCommandResponse> handler)
        => _handlers[command] = handler;

    public Task<WorkerCommandResponse> SendCommandAsync(string command, object args, CancellationToken cancellationToken = default)
    {
        lock (_callsLock)
            Calls.Add((command, args));
        if (_handlers.TryGetValue(command, out var handler))
            return Task.FromResult(handler(args));
        return Task.FromResult(new WorkerCommandResponse(false, null, $"No canned response for '{command}'."));
    }
}
