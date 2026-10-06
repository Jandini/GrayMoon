using GrayMoon.Abstractions.Worker;
using GrayMoon.Abstractions.Workspaces;
using GrayMoon.App.Data;
using GrayMoon.App.Models;
using GrayMoon.App.Repositories;
using GrayMoon.App.Services;
using GrayMoon.App.Services.GitChanges;
using GrayMoon.App.Services.WorkspaceManifest;
using GrayMoon.Application.WorkspaceManifest;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using AppWorkspaceRepository = GrayMoon.App.Repositories.WorkspaceRepository;

namespace GrayMoon.App.Tests;

public sealed class WorkspaceRepositoryOperationsTests
{
    [Fact]
    public async Task Enable_refused_when_worker_lacks_feature()
    {
        await using var fixture = await OperationsFixture.CreateAsync(workerSupportsFeature: false);
        var (workspace, root) = await fixture.SeedEnableScenarioAsync();

        var result = await fixture.Operations.EnableWorkspaceRepositoryAsync(workspace.WorkspaceId, root.RepositoryId);

        Assert.False(result.Success);
        Assert.Equal(WorkspaceRepositoryOperations.UnsupportedWorkerMessage, result.Error);
        Assert.Empty(fixture.Bridge.Sent);
        Assert.Empty(await fixture.GetLinksAsync(workspace.WorkspaceId));
    }

    [Fact]
    public async Task Enable_refused_while_features_exist()
    {
        await using var fixture = await OperationsFixture.CreateAsync();
        var (workspace, root) = await fixture.SeedEnableScenarioAsync();
        await fixture.AddFeatureAsync(workspace.WorkspaceId);

        var result = await fixture.Operations.EnableWorkspaceRepositoryAsync(workspace.WorkspaceId, root.RepositoryId);

        Assert.False(result.Success);
        Assert.Contains("Features exist", result.Error);
        Assert.Empty(fixture.Bridge.Sent);
        Assert.Empty(await fixture.GetLinksAsync(workspace.WorkspaceId));
    }

    [Fact]
    public async Task Enable_refused_when_repository_is_already_source()
    {
        await using var fixture = await OperationsFixture.CreateAsync();
        var (workspace, root) = await fixture.SeedEnableScenarioAsync();
        await fixture.Seed.LinkAsync(workspace, root, WorkspaceRepositoryRole.Source);

        var result = await fixture.Operations.EnableWorkspaceRepositoryAsync(workspace.WorkspaceId, root.RepositoryId);

        Assert.False(result.Success);
        Assert.Contains("already a Source", result.Error);
        Assert.Empty(fixture.Bridge.Sent);
        var link = Assert.Single(await fixture.GetLinksAsync(workspace.WorkspaceId));
        Assert.Equal(WorkspaceRepositoryRole.Source, link.Role);
    }

    [Fact]
    public async Task Enable_rolls_back_link_when_attach_fails()
    {
        await using var fixture = await OperationsFixture.CreateAsync();
        var (workspace, root) = await fixture.SeedEnableScenarioAsync();
        fixture.Bridge.Handler = (command, _) => command == "AttachWorkspaceRepository"
            ? new WorkerCommandResponse(true, new { success = false, errorMessage = "Root already has a different Git repository" }, null)
            : new WorkerCommandResponse(true, new { success = true }, null);

        var result = await fixture.Operations.EnableWorkspaceRepositoryAsync(workspace.WorkspaceId, root.RepositoryId);

        Assert.False(result.Success);
        Assert.Equal("Root already has a different Git repository", result.Error);
        Assert.Empty(await fixture.GetLinksAsync(workspace.WorkspaceId, WorkspaceRepositoryRole.Workspace));
        Assert.Empty(fixture.Manifest.Calls);
    }

    [Fact]
    public async Task Enable_writes_gitignore_then_manifest()
    {
        await using var fixture = await OperationsFixture.CreateAsync();
        var (workspace, root) = await fixture.SeedEnableScenarioAsync();

        var result = await fixture.Operations.EnableWorkspaceRepositoryAsync(workspace.WorkspaceId, root.RepositoryId);

        Assert.True(result.Success, result.Error);
        Assert.Equal(["gitignore", "manifest"], fixture.Manifest.Calls.ToArray());
        var link = Assert.Single(await fixture.GetLinksAsync(workspace.WorkspaceId));
        Assert.Equal(WorkspaceRepositoryRole.Workspace, link.Role);
        Assert.Equal(root.RepositoryId, link.RepositoryId);

        var attach = Assert.Single(fixture.Bridge.Sent, s => s.Command == "AttachWorkspaceRepository");
        var args = ManifestTestFixture.ToJson(attach.Args);
        Assert.Equal(workspace.WorkspaceId, args.GetProperty("workspaceId").GetInt32());
        Assert.Equal(root.RepositoryId, args.GetProperty("repositoryId").GetInt32());
        Assert.Equal(root.CloneUrl, args.GetProperty("cloneUrl").GetString());
        Assert.Equal(ManifestTestFixture.WorkspaceFolder, args.GetProperty("workspaceName").GetString());
        Assert.Equal(ManifestTestFixture.WorkspaceRoot, args.GetProperty("workspaceRoot").GetString());
    }

    [Fact]
    public async Task Disable_refused_while_features_exist()
    {
        await using var fixture = await OperationsFixture.CreateAsync();
        var (workspace, root) = await fixture.SeedEnableScenarioAsync();
        await fixture.Seed.LinkAsync(workspace, root, WorkspaceRepositoryRole.Workspace);
        await fixture.AddFeatureAsync(workspace.WorkspaceId);

        var result = await fixture.Operations.DisableWorkspaceRepositoryAsync(workspace.WorkspaceId);

        Assert.False(result.Success);
        Assert.Contains("Features exist", result.Error);
        Assert.Single(await fixture.GetLinksAsync(workspace.WorkspaceId, WorkspaceRepositoryRole.Workspace));
    }

    [Fact]
    public async Task Restore_resolves_repositories_by_normalized_url_with_different_local_ids()
    {
        await using var fixture = await OperationsFixture.CreateAsync();
        var github = await fixture.Seed.AddConnectorAsync("github", ConnectorType.GitHub, "https://api.github.com");
        // Filler rows push the ids of the repositories that matter away from any hint the manifest could carry.
        for (var i = 0; i < 5; i++)
            await fixture.Seed.AddRepositoryAsync(github, $"filler-{i}", $"https://github.com/other/filler-{i}.git");
        var root = await fixture.Seed.AddRepositoryAsync(github, "ws-root", "https://github.com/acme/ws-root.git");
        var api = await fixture.Seed.AddRepositoryAsync(github, "Api", "https://github.com/acme/Api.git");
        var web = await fixture.Seed.AddRepositoryAsync(github, "Web", "https://GitHub.com/acme/Web");

        var manifest = new WorkspaceManifest(
            1,
            new WorkspaceManifestWorkspace("restored", new WorkspaceManifestProfile("dotNetDependency", "gitVersion", "githubActions")),
            [new WorkspaceManifestConnector("github", "https://github.com/")],
            [
                new WorkspaceManifestRepository("Api", "git@github.com:acme/Api.git", "https://github.com"),
                new WorkspaceManifestRepository("Web", "https://github.com/acme/Web.git", "https://github.com"),
            ]);
        fixture.Bridge.RespondWithFile(WorkspaceManifestSerializer.Serialize(manifest));

        var result = await fixture.Operations.RestoreFromRepositoryAsync(root.RepositoryId, "restored");

        Assert.True(result.Success, result.Error);
        Assert.Null(result.Error);
        Assert.NotNull(result.WorkspaceId);
        Assert.Empty(result.UnresolvedConnectorUrls);
        Assert.Empty(result.UnresolvedRepositoryUrls);

        var links = await fixture.GetLinksAsync(result.WorkspaceId!.Value);
        Assert.Equal(
            new[] { root.RepositoryId, api.RepositoryId, web.RepositoryId }.Order().ToArray(),
            links.Select(l => l.RepositoryId).Order().ToArray());
        Assert.Equal(WorkspaceRepositoryRole.Workspace, links.Single(l => l.RepositoryId == root.RepositoryId).Role);
        Assert.All(links.Where(l => l.RepositoryId != root.RepositoryId), l => Assert.Equal(WorkspaceRepositoryRole.Source, l.Role));

        await using var db = new AppDbContext(fixture.Seed.Options);
        var workspace = await db.Workspaces.AsNoTracking().SingleAsync(w => w.WorkspaceId == result.WorkspaceId);
        Assert.Equal(WorkspaceType.DotNetDependency, workspace.Type);
        Assert.Equal(WorkspaceVersioningMode.GitVersion, workspace.VersioningMode);
        Assert.Equal(WorkspaceCiProvider.GitHubActions, workspace.CiProvider);
        Assert.Equal(["gitignore", "manifest"], fixture.Manifest.Calls.ToArray());
    }

    [Fact]
    public async Task Restore_reports_unresolved_connector_and_repository()
    {
        await using var fixture = await OperationsFixture.CreateAsync();
        var github = await fixture.Seed.AddConnectorAsync("github", ConnectorType.GitHub, "https://api.github.com");
        var root = await fixture.Seed.AddRepositoryAsync(github, "ws-root", "https://github.com/acme/ws-root.git");

        var manifest = new WorkspaceManifest(
            1,
            new WorkspaceManifestWorkspace("restored", new WorkspaceManifestProfile("basic", "none", "none")),
            [
                new WorkspaceManifestConnector("github", "https://github.com"),
                new WorkspaceManifestConnector("github", "https://ghe.other.example"),
            ],
            [new WorkspaceManifestRepository("Missing", "https://ghe.other.example/acme/Missing.git", "https://ghe.other.example")]);
        fixture.Bridge.RespondWithFile(WorkspaceManifestSerializer.Serialize(manifest));

        var result = await fixture.Operations.RestoreFromRepositoryAsync(root.RepositoryId, "restored");

        Assert.True(result.Success, result.Error);
        Assert.Equal(["https://ghe.other.example"], result.UnresolvedConnectorUrls.ToArray());
        var unresolved = Assert.Single(result.UnresolvedRepositoryUrls);
        Assert.Contains("https://ghe.other.example/acme/Missing.git", unresolved);
        Assert.Contains("https://ghe.other.example", unresolved);

        var links = await fixture.GetLinksAsync(result.WorkspaceId!.Value);
        var only = Assert.Single(links);
        Assert.Equal(WorkspaceRepositoryRole.Workspace, only.Role);
    }

    [Fact]
    public async Task Restore_sends_require_empty_root_and_cleans_up_workspace_row_on_failure()
    {
        await using var fixture = await OperationsFixture.CreateAsync();
        var github = await fixture.Seed.AddConnectorAsync("github", ConnectorType.GitHub, "https://api.github.com");
        var root = await fixture.Seed.AddRepositoryAsync(github, "ws-root", "https://github.com/acme/ws-root.git");
        fixture.Bridge.Handler = (command, _) => command == "AttachWorkspaceRepository"
            ? new WorkerCommandResponse(true, new { success = false, errorMessage = "The folder already exists and is not empty." }, null)
            : new WorkerCommandResponse(true, new { success = true }, null);

        var result = await fixture.Operations.RestoreFromRepositoryAsync(root.RepositoryId, "restored");

        Assert.False(result.Success);
        Assert.Equal("The folder already exists and is not empty.", result.Error);
        var attach = Assert.Single(fixture.Bridge.Sent, s => s.Command == "AttachWorkspaceRepository");
        Assert.True(ManifestTestFixture.ToJson(attach.Args).GetProperty("requireEmptyRoot").GetBoolean());

        await using var db = new AppDbContext(fixture.Seed.Options);
        Assert.Empty(await db.Workspaces.AsNoTracking().ToListAsync());
        Assert.Empty(await db.WorkspaceRepositories.AsNoTracking().ToListAsync());
        Assert.DoesNotContain(fixture.Bridge.Sent, s => s.Command == "RemoveWorkspace" || s.Command == "DeleteDirectory");
    }
}

internal sealed class OperationsFixture : IAsyncDisposable
{
    private readonly ManifestTestFixture _seed;

    public ManifestTestFixture Seed => _seed;

    public ScriptedWorkerBridge Bridge => _seed.Bridge;

    public RecordingManifestService Manifest { get; } = new();

    public WorkspaceRepositoryOperations Operations { get; }

    private OperationsFixture(ManifestTestFixture seed, bool workerSupportsFeature)
    {
        _seed = seed;

        // The bridge handed to WorkspaceService must not be connected to the scripted one: it only needs to accept calls.
        var workspaceService = new WorkspaceService(
            new ScriptedWorkerBridge(),
            NullLogger<WorkspaceService>.Instance,
            new AppSettingRepository(new AppDbContext(seed.Options)),
            Options.Create(new WorkspaceOptions()));
        var workspaceRepository = new AppWorkspaceRepository(
            new AppDbContext(seed.Options),
            seed.Factory,
            workspaceService,
            new WorkspaceGitChangesNotifier(NullLogger<WorkspaceGitChangesNotifier>.Instance),
            NullLogger<AppWorkspaceRepository>.Instance);

        Operations = new WorkspaceRepositoryOperations(
            seed.Factory,
            seed.Bridge,
            new FakeFeatureSupport(workerSupportsFeature),
            Manifest,
            new WorkspaceOperationRunner(NullLogger<WorkspaceOperationRunner>.Instance),
            new FakeWorkspacePathResolver(seed.Factory),
            new FakeFeatureContextResolver(),
            workspaceRepository,
            NullLogger<WorkspaceRepositoryOperations>.Instance);
    }

    public static async Task<OperationsFixture> CreateAsync(bool workerSupportsFeature = true) =>
        new(await ManifestTestFixture.CreateAsync(), workerSupportsFeature);

    /// <summary>Workspace "ws" with no links, plus an unlinked GitHub repository that can become the Workspace repository.</summary>
    public async Task<(Workspace Workspace, Repository Root)> SeedEnableScenarioAsync()
    {
        var github = await _seed.AddConnectorAsync("github", ConnectorType.GitHub, "https://api.github.com");
        var workspace = await _seed.AddWorkspaceAsync(ManifestTestFixture.WorkspaceFolder);
        var root = await _seed.AddRepositoryAsync(github, "ws-root", "https://github.com/acme/ws-root.git");
        return (workspace, root);
    }

    public async Task AddFeatureAsync(int workspaceId)
    {
        await using var db = new AppDbContext(_seed.Options);
        db.WorkspaceFeatures.Add(new WorkspaceFeature { WorkspaceId = workspaceId, Name = "feature-a" });
        await db.SaveChangesAsync();
    }

    public async Task<List<WorkspaceRepositoryLink>> GetLinksAsync(int workspaceId, WorkspaceRepositoryRole? role = null)
    {
        await using var db = new AppDbContext(_seed.Options);
        var query = db.WorkspaceRepositories.AsNoTracking().Where(l => l.WorkspaceId == workspaceId);
        if (role is { } r)
            query = query.Where(l => l.Role == r);
        return await query.ToListAsync();
    }

    public ValueTask DisposeAsync() => _seed.DisposeAsync();

    private sealed class FakeFeatureSupport(bool supported) : IWorkerFeatureSupportService
    {
        public Task<bool> SupportsAsync(string feature, CancellationToken cancellationToken = default) =>
            Task.FromResult(supported);
    }
}

/// <summary>Manifest service stand-in that records the order of the write calls and delegates parsing.</summary>
internal sealed class RecordingManifestService : IWorkspaceManifestService
{
    public List<string> Calls { get; } = [];

    public Task<WorkspaceManifest> BuildFromDatabaseAsync(int workspaceId, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public string Serialize(WorkspaceManifest manifest) => WorkspaceManifestSerializer.Serialize(manifest);

    public bool TryParse(string content, out WorkspaceManifest? manifest, out string? error) =>
        WorkspaceManifestSerializer.TryParse(content, out manifest, out error);

    public Task<OperationResult> WriteAuthoritativeManifestAsync(int workspaceId, CancellationToken cancellationToken = default)
    {
        Calls.Add("manifest");
        return Task.FromResult(OperationResult.Ok());
    }

    public Task<OperationResult> WriteManagedGitIgnoreAsync(int workspaceId, CancellationToken cancellationToken = default)
    {
        Calls.Add("gitignore");
        return Task.FromResult(OperationResult.Ok());
    }

    public Task<WorkspaceManifestDrift> DetectDriftAsync(int workspaceId, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
}
