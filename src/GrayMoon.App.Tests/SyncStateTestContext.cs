using GrayMoon.Abstractions.Agent;
using GrayMoon.App.Data;
using GrayMoon.App.Hubs;
using GrayMoon.App.Models;
using GrayMoon.App.Repositories;
using GrayMoon.App.Services;
using GrayMoon.App.Services.Queries;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace GrayMoon.App.Tests;

/// <summary>
/// Real DI container over an in-memory SQLite database, used by the write-side tests
/// (sync command handling, sync-to-default persistence, PR refresh). The only substituted
/// dependencies are the agent transport and the SignalR hub, so the services under test run
/// their production code paths against a real EF Core model.
/// </summary>
public sealed class SyncStateTestContext : IAsyncDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _provider;

    public FakeAgentBridge AgentBridge { get; }
    public FakeHubContext<WorkspaceSyncHub> HubContext { get; }
    public int WorkspaceId { get; private set; }
    public int RepositoryId { get; private set; }
    public int WorkspaceRepositoryId { get; private set; }

    private SyncStateTestContext(SqliteConnection connection, ServiceProvider provider, FakeAgentBridge agentBridge, FakeHubContext<WorkspaceSyncHub> hubContext)
    {
        _connection = connection;
        _provider = provider;
        AgentBridge = agentBridge;
        HubContext = hubContext;
    }

    public static async Task<SyncStateTestContext> CreateAsync(string? userToken = null)
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var agentBridge = new FakeAgentBridge();
        var hubContext = new FakeHubContext<WorkspaceSyncHub>();

        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.Configure<WorkspaceOptions>(o => o.MaxParallelOperations = 4);
        services.AddSingleton<IGitHubRateLimitTracker, GitHubRateLimitTracker>();
        services.AddSingleton<IGitHubETagCache, GitHubETagCache>();
        services.AddSingleton<IGitHubApiUsageRecorder, FakeGitHubApiUsageRecorder>();
        services.AddSingleton(new HttpClient());
        services.AddSingleton<IAgentBridge>(agentBridge);
        services.AddSingleton<IHubContext<WorkspaceSyncHub>>(hubContext);

        services.AddDbContext<AppDbContext>(o => o.UseSqlite(connection), ServiceLifetime.Scoped);
        services.AddDbContextFactory<AppDbContext>(o => o.UseSqlite(connection), ServiceLifetime.Singleton);

        services.AddScoped<AppSettingRepository>();
        services.AddScoped<ConnectorRepository>();
        services.AddScoped<GitHubRepositoryRepository>();
        services.AddScoped<WorkspaceRepository>();
        services.AddScoped<WorkspaceProjectRepository>();
        services.AddScoped<WorkspacePullRequestRepository>();
        services.AddScoped<WorkspaceFileVersionConfigRepository>();
        services.AddScoped<WorkspaceRepositoryCustomDependencyRepository>();

        services.AddScoped<WorkspaceService>();
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
        services.AddScoped<WorkspaceRepositoryLinkListQueryService>();
        services.AddScoped<SyncCommandHandler>();

        var provider = services.BuildServiceProvider();

        var ctx = new SyncStateTestContext(connection, provider, agentBridge, hubContext);
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

        var workspace = new Workspace { Name = "test-ws" };
        db.Workspaces.Add(workspace);
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

        WorkspaceId = workspace.WorkspaceId;
        RepositoryId = repository.RepositoryId;
        WorkspaceRepositoryId = link.WorkspaceRepositoryId;
    }

    public AsyncServiceScope CreateScope() => _provider.CreateAsyncScope();

    public T Resolve<T>() where T : notnull => _provider.GetRequiredService<T>();

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

    public IReadOnlyList<(string Method, object?[] Args)> Broadcasts => HubContext.ClientsImpl.AllProxy.Sent;

    public async ValueTask DisposeAsync()
    {
        await _provider.DisposeAsync();
        await _connection.DisposeAsync();
    }
}

/// <summary>Agent transport stub. Tests register a canned response per command name; unregistered commands fail like a disconnected agent would.</summary>
public sealed class FakeAgentBridge : IAgentBridge
{
    private readonly Dictionary<string, Func<object, AgentCommandResponse>> _handlers = new(StringComparer.OrdinalIgnoreCase);

    public bool IsAgentConnected { get; set; } = true;
    public List<(string Command, object Args)> Calls { get; } = [];

    public void Respond(string command, object? data, bool success = true, string? error = null)
        => _handlers[command] = _ => new AgentCommandResponse(success, data, error);

    public void Respond(string command, Func<object, AgentCommandResponse> handler)
        => _handlers[command] = handler;

    public Task<AgentCommandResponse> SendCommandAsync(string command, object args, CancellationToken cancellationToken = default)
    {
        Calls.Add((command, args));
        if (_handlers.TryGetValue(command, out var handler))
            return Task.FromResult(handler(args));
        return Task.FromResult(new AgentCommandResponse(false, null, $"No canned response for '{command}'."));
    }
}
