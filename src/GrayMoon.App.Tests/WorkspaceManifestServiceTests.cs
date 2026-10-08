using System.Text.Json;
using GrayMoon.Abstractions.Worker;
using GrayMoon.Abstractions.Workspaces;
using GrayMoon.Application.Features;
using GrayMoon.App.Data;
using GrayMoon.App.Models;
using GrayMoon.App.Services.WorkspaceManifest;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using WorkspaceManifestConnector = GrayMoon.Application.WorkspaceManifest.WorkspaceManifestConnector;
using WorkspaceManifestRepository = GrayMoon.Application.WorkspaceManifest.WorkspaceManifestRepository;

namespace GrayMoon.App.Tests;

public sealed class WorkspaceManifestServiceTests
{
    [Fact]
    public async Task Build_excludes_workspace_role_repository_and_nuget_connectors()
    {
        await using var fixture = await ManifestTestFixture.CreateAsync();
        var github = await fixture.AddConnectorAsync("github", ConnectorType.GitHub, "https://api.github.com");
        var ghe = await fixture.AddConnectorAsync("ghe", ConnectorType.GitHub, "https://ghe.example.com/api/v3");
        await fixture.AddConnectorAsync("nuget", ConnectorType.NuGet, "https://api.nuget.org/v3/index.json");
        var workspace = await fixture.AddWorkspaceAsync("ws");
        var api = await fixture.AddRepositoryAsync(github, "Api", "https://github.com/acme/Api.git");
        var web = await fixture.AddRepositoryAsync(github, "Web", "https://github.com/acme/Web.git");
        var root = await fixture.AddRepositoryAsync(ghe, "ws-root", "https://ghe.example.com/acme/ws-root.git");
        await fixture.LinkAsync(workspace, api, WorkspaceRepositoryRole.Source);
        await fixture.LinkAsync(workspace, web, WorkspaceRepositoryRole.Source);
        await fixture.LinkAsync(workspace, root, WorkspaceRepositoryRole.Workspace);

        var manifest = await fixture.CreateService().BuildFromDatabaseAsync(workspace.WorkspaceId);

        Assert.Equal(["Api", "Web"], manifest.Repositories.Select(r => r.Name).ToArray());
        var connector = Assert.Single(manifest.Connectors);
        Assert.Equal("github", connector.Type);
        Assert.Equal("https://github.com", connector.Url);
        Assert.Equal("ws", manifest.Workspace.Name);
    }

    [Fact]
    public async Task Build_connector_url_derived_from_api_base_url()
    {
        await using var fixture = await ManifestTestFixture.CreateAsync();
        var github = await fixture.AddConnectorAsync("github", ConnectorType.GitHub, "https://api.github.com/");
        var ghe = await fixture.AddConnectorAsync("ghe", ConnectorType.GitHub, "https://GHE.example.com/api/v3/");
        var workspace = await fixture.AddWorkspaceAsync("ws");
        var api = await fixture.AddRepositoryAsync(github, "Api", "https://github.com/acme/Api.git");
        var internalRepo = await fixture.AddRepositoryAsync(ghe, "Internal", "https://ghe.example.com/acme/Internal.git");
        await fixture.LinkAsync(workspace, api, WorkspaceRepositoryRole.Source);
        await fixture.LinkAsync(workspace, internalRepo, WorkspaceRepositoryRole.Source);

        var manifest = await fixture.CreateService().BuildFromDatabaseAsync(workspace.WorkspaceId);

        Assert.Equal(
            ["https://ghe.example.com", "https://github.com"],
            manifest.Connectors.Select(c => c.Url).ToArray());
        Assert.Equal("https://github.com", manifest.Repositories.Single(r => r.Name == "Api").ConnectorUrl);
        Assert.Equal("https://ghe.example.com", manifest.Repositories.Single(r => r.Name == "Internal").ConnectorUrl);
    }

    [Fact]
    public async Task Write_manifest_sends_WriteRepositoryFile_to_workspace_repository_root()
    {
        await using var fixture = await ManifestTestFixture.CreateAsync();
        var workspace = await fixture.SeedWorkspaceWithRootRepositoryAsync();
        var service = fixture.CreateService();

        var result = await service.WriteAuthoritativeManifestAsync(workspace.WorkspaceId);

        Assert.True(result.Success, result.Error);
        var sent = Assert.Single(fixture.Bridge.Sent);
        Assert.Equal("WriteRepositoryFile", sent.Command);
        var args = ManifestTestFixture.ToJson(sent.Args);
        Assert.Equal(".graymoon.json", args.GetProperty("filePath").GetString());
        Assert.Equal("ws-root", args.GetProperty("repositoryName").GetString());
        Assert.Equal("ws-root", args.GetProperty("workspaceRepositoryName").GetString());
        Assert.Equal(ManifestTestFixture.WorkspaceRoot, args.GetProperty("workspaceRoot").GetString());
        Assert.Equal(ManifestTestFixture.WorkspaceFolder, args.GetProperty("workspaceName").GetString());
        Assert.True(args.GetProperty("onlyIfChanged").GetBoolean());

        var expected = service.Serialize(await service.BuildFromDatabaseAsync(workspace.WorkspaceId));
        Assert.Equal(expected, args.GetProperty("content").GetString());
    }

    [Fact]
    public async Task Write_manifest_is_noop_without_workspace_repository()
    {
        await using var fixture = await ManifestTestFixture.CreateAsync();
        var workspace = await fixture.AddWorkspaceAsync("ws");

        var manifestResult = await fixture.CreateService().WriteAuthoritativeManifestAsync(workspace.WorkspaceId);
        var gitIgnoreResult = await fixture.CreateService().WriteManagedGitIgnoreAsync(workspace.WorkspaceId);

        Assert.True(manifestResult.Success);
        Assert.Equal("No Workspace repository", manifestResult.Error);
        Assert.True(gitIgnoreResult.Success);
        Assert.Empty(fixture.Bridge.Sent);
    }

    [Fact]
    public async Task Detect_drift_false_when_file_equals_database()
    {
        await using var fixture = await ManifestTestFixture.CreateAsync();
        var workspace = await fixture.SeedWorkspaceWithRootRepositoryAsync();
        var service = fixture.CreateService();
        fixture.Bridge.RespondWithFile(service.Serialize(await service.BuildFromDatabaseAsync(workspace.WorkspaceId)));

        var drift = await service.DetectDriftAsync(workspace.WorkspaceId);

        Assert.False(drift.HasDrift);
        Assert.Null(drift.ParseError);
        Assert.Null(await fixture.GetDriftTimestampAsync(workspace.WorkspaceId));
    }

    [Fact]
    public async Task Detect_drift_true_when_repository_added_in_file()
    {
        await using var fixture = await ManifestTestFixture.CreateAsync();
        var workspace = await fixture.SeedWorkspaceWithRootRepositoryAsync();
        var service = fixture.CreateService();
        var databaseManifest = await service.BuildFromDatabaseAsync(workspace.WorkspaceId);
        var withExtra = databaseManifest with
        {
            Repositories = [.. databaseManifest.Repositories,
                new WorkspaceManifestRepository("Extra", "https://github.com/acme/Extra.git", "https://github.com")]
        };
        fixture.Bridge.RespondWithFile(service.Serialize(withExtra));

        var drift = await service.DetectDriftAsync(workspace.WorkspaceId);

        Assert.True(drift.HasDrift);
        Assert.Equal(["Extra"], drift.AddedRepositories.ToArray());
        Assert.Empty(drift.RemovedRepositories);
        Assert.NotNull(await fixture.GetDriftTimestampAsync(workspace.WorkspaceId));
    }

    [Fact]
    public async Task Detect_drift_true_on_parse_error_with_reason()
    {
        await using var fixture = await ManifestTestFixture.CreateAsync();
        var workspace = await fixture.SeedWorkspaceWithRootRepositoryAsync();
        fixture.Bridge.RespondWithFile("{ this is not json");

        var drift = await fixture.CreateService().DetectDriftAsync(workspace.WorkspaceId);

        Assert.True(drift.HasDrift);
        Assert.False(string.IsNullOrWhiteSpace(drift.ParseError));
        Assert.NotNull(await fixture.GetDriftTimestampAsync(workspace.WorkspaceId));
    }

    [Fact]
    public async Task Detect_drift_refuses_feature_context()
    {
        await using var fixture = await ManifestTestFixture.CreateAsync(contextIsSpecialWorkspace: false);
        var workspace = await fixture.SeedWorkspaceWithRootRepositoryAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.CreateService().DetectDriftAsync(workspace.WorkspaceId));

        Assert.Empty(fixture.Bridge.Sent);
    }

    [Fact]
    public async Task Detect_drift_ignores_url_formatting_differences()
    {
        await using var fixture = await ManifestTestFixture.CreateAsync();
        var workspace = await fixture.SeedWorkspaceWithRootRepositoryAsync();
        var service = fixture.CreateService();
        var databaseManifest = await service.BuildFromDatabaseAsync(workspace.WorkspaceId);
        var reformatted = databaseManifest with
        {
            Connectors = [new WorkspaceManifestConnector("github", "https://GitHub.com/")],
            Repositories = databaseManifest.Repositories
                .Select(r => new WorkspaceManifestRepository(r.Name, "git@GitHub.com:acme/" + r.Name + ".git", "https://GitHub.com/"))
                .ToList()
        };
        fixture.Bridge.RespondWithFile(service.Serialize(reformatted));

        var drift = await service.DetectDriftAsync(workspace.WorkspaceId);

        Assert.False(drift.HasDrift);
    }
}

/// <summary>Records every Worker command and answers from a script; shared by the Workspace repository tests.</summary>
internal sealed class ScriptedWorkerBridge : IWorkerBridge
{
    public bool IsWorkerConnected { get; set; } = true;

    /// <summary>Simulates a Worker that cannot run normal commands (for example a version mismatch).</summary>
    public string? UnavailableReason { get; set; }

    public string? GetUnavailableReason() => UnavailableReason ?? (IsWorkerConnected ? null : "Worker not connected.");

    public List<(string Command, object Args)> Sent { get; } = [];

    public Func<string, object, WorkerCommandResponse> Handler { get; set; } =
        (_, _) => new WorkerCommandResponse(true, new { success = true, removed = true }, null);

    public Task<WorkerCommandResponse> SendCommandAsync(string command, object args, CancellationToken cancellationToken = default)
    {
        Sent.Add((command, args));
        return Task.FromResult(Handler(command, args));
    }

    /// <summary>Answers every GetFileContents with <paramref name="content"/> and every other command with success.</summary>
    public void RespondWithFile(string content) =>
        Handler = (command, _) => command == "GetFileContents"
            ? new WorkerCommandResponse(true, new { content }, null)
            : new WorkerCommandResponse(true, new { success = true, written = true, removed = true }, null);
}

internal sealed class FakeFeatureContextResolver(bool isSpecialWorkspace = true) : IWorkspaceFeatureContextResolver
{
    public Task<WorkspaceFeatureContextId> GetOrCreateSpecialWorkspaceContextIdAsync(int workspaceId, CancellationToken cancellationToken = default) =>
        Task.FromResult(new WorkspaceFeatureContextId(1));

    public Task<WorkspaceFeatureContextInfo> GetRequiredAsync(WorkspaceFeatureContextId contextId, int? expectedWorkspaceId = null, CancellationToken cancellationToken = default) =>
        Task.FromResult(new WorkspaceFeatureContextInfo
        {
            ContextId = contextId,
            WorkspaceId = expectedWorkspaceId ?? 0,
            IsSpecialWorkspace = isSpecialWorkspace,
        });

    public Task<IReadOnlyList<WorkspaceFeatureContextInfo>> ListForWorkspaceAsync(int workspaceId, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
}

/// <summary>Path resolver that reads the Workspace-role repository name from the database like the real one.</summary>
internal sealed class FakeWorkspacePathResolver(IDbContextFactory<AppDbContext> dbContextFactory) : IWorkspaceContextPathResolver
{
    public Task<string> GetContextRootAsync(WorkspaceFeatureContextId contextId, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<string> GetRepositoryPathAsync(WorkspaceFeatureContextId contextId, int workspaceRepositoryId, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public async Task<WorkerWorkspaceArgs> GetWorkerArgsAsync(WorkspaceFeatureContextId contextId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        // The fake has a single special context per test database, so the first Workspace-role link is the one.
        var name = await db.WorkspaceRepositories
            .AsNoTracking()
            .Where(l => l.Role == WorkspaceRepositoryRole.Workspace)
            .Select(l => l.Repository!.RepositoryName)
            .FirstOrDefaultAsync(cancellationToken);
        return new WorkerWorkspaceArgs(ManifestTestFixture.WorkspaceRoot, ManifestTestFixture.WorkspaceFolder, name);
    }
}

internal sealed class ManifestTestFixture : IAsyncDisposable
{
    public const string WorkspaceRoot = "C:\\work";
    public const string WorkspaceFolder = "ws";

    private readonly SqliteConnection _connection;
    private readonly bool _contextIsSpecialWorkspace;

    public DbContextOptions<AppDbContext> Options { get; }

    public IDbContextFactory<AppDbContext> Factory { get; }

    public ScriptedWorkerBridge Bridge { get; } = new();

    private ManifestTestFixture(SqliteConnection connection, DbContextOptions<AppDbContext> options, bool contextIsSpecialWorkspace)
    {
        _connection = connection;
        _contextIsSpecialWorkspace = contextIsSpecialWorkspace;
        Options = options;
        Factory = new GitChangesTestDbContext.TestDbContextFactory(options);
    }

    public static async Task<ManifestTestFixture> CreateAsync(bool contextIsSpecialWorkspace = true)
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using (var db = new AppDbContext(options))
            await db.Database.EnsureCreatedAsync();
        return new ManifestTestFixture(connection, options, contextIsSpecialWorkspace);
    }

    public WorkspaceManifestService CreateService() => new(
        Factory,
        Bridge,
        new FakeWorkspacePathResolver(Factory),
        new FakeFeatureContextResolver(_contextIsSpecialWorkspace),
        NullLogger<WorkspaceManifestService>.Instance);

    public static JsonElement ToJson(object value) => JsonSerializer.SerializeToElement(value);

    public async Task<Connector> AddConnectorAsync(string name, ConnectorType type, string apiBaseUrl)
    {
        await using var db = new AppDbContext(Options);
        var connector = new Connector
        {
            ConnectorName = name,
            ConnectorType = type,
            ApiBaseUrl = apiBaseUrl,
            IsActive = true,
            IsHealthy = true,
        };
        db.Connectors.Add(connector);
        await db.SaveChangesAsync();
        return connector;
    }

    public async Task<Workspace> AddWorkspaceAsync(string name)
    {
        await using var db = new AppDbContext(Options);
        var workspace = new Workspace
        {
            Name = name,
            Type = WorkspaceType.Basic,
            VersioningMode = WorkspaceVersioningMode.None,
            CiProvider = WorkspaceCiProvider.None,
        };
        db.Workspaces.Add(workspace);
        await db.SaveChangesAsync();
        return workspace;
    }

    public async Task<Repository> AddRepositoryAsync(Connector connector, string name, string cloneUrl)
    {
        await using var db = new AppDbContext(Options);
        var repository = new Repository
        {
            ConnectorId = connector.ConnectorId,
            RepositoryName = name,
            CloneUrl = cloneUrl,
        };
        db.Repositories.Add(repository);
        await db.SaveChangesAsync();
        return repository;
    }

    public async Task<WorkspaceRepositoryLink> LinkAsync(Workspace workspace, Repository repository, WorkspaceRepositoryRole role)
    {
        await using var db = new AppDbContext(Options);
        var link = new WorkspaceRepositoryLink
        {
            WorkspaceId = workspace.WorkspaceId,
            RepositoryId = repository.RepositoryId,
            Role = role,
        };
        db.WorkspaceRepositories.Add(link);
        await db.SaveChangesAsync();
        return link;
    }

    /// <summary>Workspace "ws" with Source repositories Api and Web (github.com) and Workspace repository ws-root.</summary>
    public async Task<Workspace> SeedWorkspaceWithRootRepositoryAsync()
    {
        var github = await AddConnectorAsync("github", ConnectorType.GitHub, "https://api.github.com");
        var workspace = await AddWorkspaceAsync(WorkspaceFolder);
        var api = await AddRepositoryAsync(github, "Api", "https://github.com/acme/Api.git");
        var web = await AddRepositoryAsync(github, "Web", "https://github.com/acme/Web.git");
        var root = await AddRepositoryAsync(github, "ws-root", "https://github.com/acme/ws-root.git");
        await LinkAsync(workspace, api, WorkspaceRepositoryRole.Source);
        await LinkAsync(workspace, web, WorkspaceRepositoryRole.Source);
        await LinkAsync(workspace, root, WorkspaceRepositoryRole.Workspace);
        return workspace;
    }

    public async Task<DateTime?> GetDriftTimestampAsync(int workspaceId)
    {
        await using var db = new AppDbContext(Options);
        return await db.Workspaces
            .AsNoTracking()
            .Where(w => w.WorkspaceId == workspaceId)
            .Select(w => w.ManifestDriftDetectedAt)
            .SingleAsync();
    }

    public async ValueTask DisposeAsync() => await _connection.DisposeAsync();
}
