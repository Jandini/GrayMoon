using GrayMoon.Abstractions.Worker;
using GrayMoon.Abstractions.Workspaces;
using GrayMoon.App.Components.Modals;
using GrayMoon.App.Data;
using GrayMoon.App.Models;
using GrayMoon.Application;
using GrayMoon.Application.WorkspaceManifest;
using GrayMoon.App.Services.WorkspaceManifest;
using Microsoft.EntityFrameworkCore;

namespace GrayMoon.App.Tests;

/// <summary>
/// What the "Restore from repository" dialog adds on top of RestoreFromRepositoryAsync (covered by
/// WorkspaceRepositoryOperationsTests): name defaulting, validation, and the Sync that follows a successful restore.
/// </summary>
public sealed class RestoreWorkspaceFlowTests
{
    [Fact]
    public async Task Restore_creates_workspace_links_and_profile_from_manifest()
    {
        await using var fixture = await OperationsFixture.CreateAsync();
        var github = await fixture.Seed.AddConnectorAsync("github", ConnectorType.GitHub, "https://api.github.com");
        var root = await fixture.Seed.AddRepositoryAsync(github, "ws-root", "https://github.com/acme/ws-root.git");
        var api = await fixture.Seed.AddRepositoryAsync(github, "Api", "https://github.com/acme/Api.git");
        fixture.Bridge.RespondWithFile(WorkspaceManifestSerializer.Serialize(new WorkspaceManifest(
            1,
            new WorkspaceManifestWorkspace("AVR", new WorkspaceManifestProfile("dotNetDependency", "gitVersion", "none")),
            [new WorkspaceManifestConnector("github", "https://github.com")],
            [new WorkspaceManifestRepository("Api", "https://github.com/acme/Api.git", "https://github.com")])));

        var sync = new SyncRecorder(fixture);
        var outcome = await RestoreWorkspaceFlow.RunAsync(
            fixture.Operations, root.RepositoryId, "  AVR  ", sync.RequestAsync, progress: null, CancellationToken.None);

        Assert.True(outcome.Restore.Success, outcome.Restore.Error);
        Assert.True(outcome.SyncRequested);
        Assert.Null(outcome.SyncError);
        var workspaceId = outcome.Restore.WorkspaceId!.Value;

        // The name is trimmed, and Sync saw a fully restored Workspace (links already written).
        Assert.Equal([workspaceId], sync.WorkspaceIds);
        Assert.Equal(2, sync.LinkCountAtSync);

        var links = await fixture.GetLinksAsync(workspaceId);
        Assert.Equal(WorkspaceRepositoryRole.Workspace, links.Single(l => l.RepositoryId == root.RepositoryId).Role);
        Assert.Equal(WorkspaceRepositoryRole.Source, links.Single(l => l.RepositoryId == api.RepositoryId).Role);

        await using var db = new AppDbContext(fixture.Seed.Options);
        var workspace = await db.Workspaces.AsNoTracking().SingleAsync(w => w.WorkspaceId == workspaceId);
        Assert.Equal("AVR", workspace.Name);
        Assert.Equal(WorkspaceType.DotNetDependency, workspace.Type);
        Assert.Equal(WorkspaceVersioningMode.GitVersion, workspace.VersioningMode);
        Assert.Equal(WorkspaceCiProvider.None, workspace.CiProvider);

        Assert.False(RestoreResultPanel.From(outcome).HasContent);
    }

    [Fact]
    public async Task Restore_without_manifest_creates_bare_workspace()
    {
        await using var fixture = await OperationsFixture.CreateAsync();
        var github = await fixture.Seed.AddConnectorAsync("github", ConnectorType.GitHub, "https://api.github.com");
        var root = await fixture.Seed.AddRepositoryAsync(github, "ws-root", "https://github.com/acme/ws-root.git");
        fixture.Bridge.Handler = (command, _) => command == "GetFileContents"
            ? new WorkerCommandResponse(true, new { errorMessage = "File not found: .graymoon.json" }, null)
            : new WorkerCommandResponse(true, new { success = true }, null);

        var sync = new SyncRecorder(fixture);
        var outcome = await RestoreWorkspaceFlow.RunAsync(
            fixture.Operations, root.RepositoryId, "bare", sync.RequestAsync, progress: null, CancellationToken.None);

        Assert.True(outcome.Restore.Success, outcome.Restore.Error);
        Assert.StartsWith("Restored without definition:", outcome.Restore.Error);
        Assert.Equal([outcome.Restore.WorkspaceId!.Value], sync.WorkspaceIds);

        var links = await fixture.GetLinksAsync(outcome.Restore.WorkspaceId!.Value);
        var only = Assert.Single(links);
        Assert.Equal(WorkspaceRepositoryRole.Workspace, only.Role);

        await using var db = new AppDbContext(fixture.Seed.Options);
        var workspace = await db.Workspaces.AsNoTracking().SingleAsync();
        Assert.Equal(WorkspaceType.Basic, workspace.Type);
        Assert.Equal(WorkspaceVersioningMode.None, workspace.VersioningMode);
        Assert.Equal(WorkspaceCiProvider.None, workspace.CiProvider);

        // The panel carries the warning but no import guidance (nothing was unresolved).
        var panel = RestoreResultPanel.From(outcome);
        Assert.True(panel.HasContent);
        Assert.False(panel.ShowImportGuidance);
        Assert.StartsWith("Restored without definition:", panel.Warning);
    }

    [Fact]
    public async Task Restore_lists_unresolved_repositories_and_still_succeeds()
    {
        await using var fixture = await OperationsFixture.CreateAsync();
        var github = await fixture.Seed.AddConnectorAsync("github", ConnectorType.GitHub, "https://api.github.com");
        var root = await fixture.Seed.AddRepositoryAsync(github, "ws-root", "https://github.com/acme/ws-root.git");
        fixture.Bridge.RespondWithFile(WorkspaceManifestSerializer.Serialize(new WorkspaceManifest(
            1,
            new WorkspaceManifestWorkspace("restored", new WorkspaceManifestProfile("basic", "none", "none")),
            [new WorkspaceManifestConnector("github", "https://ghe.other.example")],
            [new WorkspaceManifestRepository("Missing", "https://ghe.other.example/acme/Missing.git", "https://ghe.other.example")])));

        var sync = new SyncRecorder(fixture);
        var outcome = await RestoreWorkspaceFlow.RunAsync(
            fixture.Operations, root.RepositoryId, "restored", sync.RequestAsync, progress: null, CancellationToken.None);

        Assert.True(outcome.Restore.Success, outcome.Restore.Error);
        Assert.True(outcome.SyncRequested);

        var panel = RestoreResultPanel.From(outcome);
        Assert.True(panel.HasContent);
        Assert.True(panel.ShowImportGuidance);
        Assert.Equal(["https://ghe.other.example"], panel.UnresolvedConnectors.ToArray());
        var unresolved = Assert.Single(panel.UnresolvedRepositories);
        Assert.Equal("https://ghe.other.example/acme/Missing.git (connector https://ghe.other.example)", unresolved);
        Assert.Equal(
            "Import these repositories through their connector, then use Review on the Repositories page.",
            RestoreResultPanel.ImportGuidance);
    }

    [Fact]
    public async Task Sync_is_not_requested_when_restore_fails()
    {
        await using var fixture = await OperationsFixture.CreateAsync();
        var github = await fixture.Seed.AddConnectorAsync("github", ConnectorType.GitHub, "https://api.github.com");
        var root = await fixture.Seed.AddRepositoryAsync(github, "ws-root", "https://github.com/acme/ws-root.git");
        fixture.Bridge.Handler = (command, _) => command == "AttachWorkspaceRepository"
            ? new WorkerCommandResponse(true, new { success = false, errorMessage = "The folder already exists and is not empty." }, null)
            : new WorkerCommandResponse(true, new { success = true }, null);

        var sync = new SyncRecorder(fixture);
        var outcome = await RestoreWorkspaceFlow.RunAsync(
            fixture.Operations, root.RepositoryId, "restored", sync.RequestAsync, progress: null, CancellationToken.None);

        Assert.False(outcome.Restore.Success);
        Assert.False(outcome.SyncRequested);
        Assert.Empty(sync.WorkspaceIds);
    }

    [Fact]
    public async Task A_failing_sync_keeps_the_restored_workspace_and_is_reported()
    {
        await using var fixture = await OperationsFixture.CreateAsync();
        var github = await fixture.Seed.AddConnectorAsync("github", ConnectorType.GitHub, "https://api.github.com");
        var root = await fixture.Seed.AddRepositoryAsync(github, "ws-root", "https://github.com/acme/ws-root.git");
        fixture.Bridge.Handler = (command, _) => command == "GetFileContents"
            ? new WorkerCommandResponse(true, new { errorMessage = "File not found: .graymoon.json" }, null)
            : new WorkerCommandResponse(true, new { success = true }, null);

        var outcome = await RestoreWorkspaceFlow.RunAsync(
            fixture.Operations,
            root.RepositoryId,
            "restored",
            (_, _, _) => throw new InvalidOperationException("Worker is busy."),
            progress: null,
            CancellationToken.None);

        Assert.True(outcome.Restore.Success);
        Assert.True(outcome.SyncRequested);
        Assert.Equal("Worker is busy.", outcome.SyncError);
        Assert.Single(await fixture.GetLinksAsync(outcome.Restore.WorkspaceId!.Value));
        Assert.Contains("Worker is busy.", RestoreResultPanel.From(outcome).Warning);
    }

    [Theory]
    [InlineData("acme/Avr", "Avr")]
    [InlineData("  acme/team/Avr.Api ", "Avr.Api")]
    [InlineData("Avr", "Avr")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void Default_name_is_the_repository_name(string? displayName, string expected) =>
        Assert.Equal(expected, RestoreWorkspaceFlow.DefaultWorkspaceName(displayName));

    [Fact]
    public void Name_must_be_present_a_valid_folder_name_and_unique_ignoring_case()
    {
        string[] existing = ["Avr", "Other"];

        Assert.Null(RestoreWorkspaceFlow.ValidateName("New", existing));
        Assert.Null(RestoreWorkspaceFlow.ValidateName("  New  ", existing));
        Assert.Equal("Workspace name is required.", RestoreWorkspaceFlow.ValidateName("   ", existing));
        Assert.Equal("Workspace name is required.", RestoreWorkspaceFlow.ValidateName(null, existing));
        Assert.Equal("Workspace name already exists.", RestoreWorkspaceFlow.ValidateName("avr", existing));
        Assert.Equal("Workspace name already exists.", RestoreWorkspaceFlow.ValidateName(" OTHER ", existing));
        Assert.NotNull(RestoreWorkspaceFlow.ValidateName("a/b", existing));
        Assert.NotNull(RestoreWorkspaceFlow.ValidateName("..", existing));
    }

    [Fact]
    public void Folder_must_be_absent_or_empty()
    {
        const string path = "C:\\work\\Avr";

        var absent = RestoreWorkspaceFlow.EvaluateFolder(false, 0, path);
        Assert.Null(absent.Error);
        Assert.Null(absent.Note);

        var empty = RestoreWorkspaceFlow.EvaluateFolder(true, 0, path);
        Assert.Null(empty.Error);
        Assert.Contains(path, empty.Note);

        var occupied = RestoreWorkspaceFlow.EvaluateFolder(true, 3, path);
        Assert.NotNull(occupied.Error);
        Assert.Contains(path, occupied.Error);
    }

    /// <summary>Stands in for the full Sync: records the Workspace ids and how many links existed when it was asked.</summary>
    private sealed class SyncRecorder(OperationsFixture fixture)
    {
        public List<int> WorkspaceIds { get; } = [];

        public int LinkCountAtSync { get; private set; }

        public async Task RequestAsync(int workspaceId, IProgress<OperationProgress>? progress, CancellationToken cancellationToken)
        {
            WorkspaceIds.Add(workspaceId);
            LinkCountAtSync = (await fixture.GetLinksAsync(workspaceId)).Count;
        }
    }
}
