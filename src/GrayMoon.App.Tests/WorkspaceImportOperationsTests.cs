using GrayMoon.Abstractions.Worker;
using GrayMoon.Abstractions.Workspaces;
using GrayMoon.App.Components.Modals;
using GrayMoon.App.Data;
using GrayMoon.App.Models;
using GrayMoon.App.Services.Application;
using GrayMoon.Application;
using GrayMoon.Application.Workspaces;
using GrayMoon.Common.Manifest;
using Microsoft.EntityFrameworkCore;

namespace GrayMoon.App.Tests;

/// <summary>
/// Import from disk: a folder that is already there becomes a Workspace. Its own path, apart from Add (new, empty)
/// and Restore (clone). Nothing in the folder is deleted, and a failed import leaves nothing of the Workspace behind.
/// </summary>
public sealed class WorkspaceImportOperationsTests
{
    private const string Folder = "existing";
    private const string RootUrl = "https://github.com/acme/ws-root.git";
    private const string ApiUrl = "https://github.com/acme/Api.git";
    private const string WebUrl = "https://github.com/acme/Web.git";

    private static readonly ImportWorkspaceOptions DotNet =
        new(WorkspaceType.DotNetDependency, WorkspaceVersioningMode.GitVersion, WorkspaceCiProvider.GitHubActions);

    // ---- Listing ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_folder_list_leaves_out_folders_that_are_a_workspace_already()
    {
        await using var fixture = await NewFixtureAsync();
        await fixture.Seed.AddWorkspaceAsync("Taken");
        fixture.Bridge.Handler = (command, _) => command == WorkerHubMethods.ListWorkspaceRootFolders
            ? new WorkerCommandResponse(true, new { folders = new[] { "zeta", "taken", "Alpha" } }, null)
            : new WorkerCommandResponse(true, new { success = true }, null);

        var list = await Import(fixture).ListImportableFoldersAsync();

        Assert.Null(list.Error);
        Assert.Equal(["Alpha", "zeta"], list.Folders);
    }

    // ---- A Workspace repository with a definition ---------------------------------------------------------------

    [Fact]
    public async Task A_folder_with_a_definition_is_previewed_from_the_definition_without_changing_anything()
    {
        await using var fixture = await NewFixtureAsync();
        await SeedCatalogAsync(fixture);
        fixture.Bridge.Handler = Inspect(
            rootOrigin: RootUrl,
            definition: Definition(("dotNetDependency", "gitVersion", "githubActions"), ("Api", ApiUrl), ("Web", WebUrl)),
            ("Api", ApiUrl),
            ("Scratch", "https://github.com/acme/Scratch.git"));

        var preview = await Import(fixture).PreflightImportAsync(Folder);

        Assert.True(preview.Success, preview.Error);
        Assert.Equal(ImportFolderShape.WorkspaceRepository, preview.Shape);
        Assert.True(preview.ProfileFromDefinition);
        Assert.Equal(WorkspaceType.DotNetDependency, preview.WorkspaceType);
        Assert.Equal(WorkspaceVersioningMode.GitVersion, preview.VersioningMode);
        Assert.Equal("ws-root", preview.WorkspaceRepository);
        Assert.Equal(["Api"], preview.LinkedOnDisk);
        // Listed in the definition but not on disk: the Sync clones it.
        Assert.Equal(["Web"], preview.ToClone);
        Assert.Contains(preview.NotLinked, line => line.StartsWith("Scratch:") && line.Contains("not imported into GrayMoon"));
        Assert.Equal(2, preview.RepositoryCount);

        Assert.DoesNotContain(fixture.Bridge.Sent, s => s.Command != WorkerHubMethods.InspectWorkspaceFolder);
        await fixture.AssertNoWorkspaceAsync();
    }

    [Fact]
    public async Task A_folder_with_a_definition_is_imported_by_adopting_its_repository_and_applying_the_definition()
    {
        await using var fixture = await NewFixtureAsync();
        var (root, api, web) = await SeedCatalogAsync(fixture);
        var definition = Definition(("dotNetDependency", "gitVersion", "githubActions"), ("Api", ApiUrl), ("Web", WebUrl));
        fixture.Bridge.Handler = Inspect(RootUrl, definition, ("Api", ApiUrl));

        var result = await Import(fixture).ImportFromDiskAsync(Folder, DotNet);

        Assert.True(result.Success, result.Error);
        await using var db = new AppDbContext(fixture.Seed.Options);
        var workspace = await db.Workspaces.AsNoTracking().SingleAsync();
        Assert.Equal(Folder, workspace.Name);
        Assert.Equal(WorkspaceType.DotNetDependency, workspace.Type);
        Assert.Equal(WorkspaceVersioningMode.GitVersion, workspace.VersioningMode);
        Assert.Equal(result.WorkspaceId, workspace.WorkspaceId);

        var links = await fixture.GetLinksAsync(workspace.WorkspaceId);
        Assert.Equal(root.RepositoryId, Assert.Single(links, l => l.Role == WorkspaceRepositoryRole.Workspace).RepositoryId);
        Assert.Equal(
            new[] { api.RepositoryId, web.RepositoryId }.Order(),
            links.Where(l => l.Role == WorkspaceRepositoryRole.Source).Select(l => l.RepositoryId).Order());

        // The repository in the folder is adopted, never cloned over, and the folder is never discarded.
        var attach = ManifestTestFixture.ToJson(Assert.Single(fixture.Bridge.Sent, s => s.Command == WorkerHubMethods.AttachWorkspaceRepository).Args);
        Assert.False(attach.GetProperty("requireEmptyRoot").GetBoolean());
        Assert.DoesNotContain(fixture.Bridge.Sent, s => s.Command == WorkerHubMethods.DiscardWorkspaceRoot);
        Assert.DoesNotContain(fixture.Bridge.Sent, s => s.Command == WorkerHubMethods.DetachRepositories);
    }

    [Fact]
    public async Task A_workspace_repository_that_gray_moon_does_not_know_stops_the_import_and_points_to_connectors()
    {
        await using var fixture = await NewFixtureAsync();
        fixture.Bridge.Handler = Inspect("https://github.com/elsewhere/unknown.git", Definition(("basic", "none", "none")));

        var preview = await Import(fixture).PreflightImportAsync(Folder);
        var result = await Import(fixture).ImportFromDiskAsync(Folder, DotNet);

        Assert.False(preview.Success);
        Assert.True(preview.NeedsConnectors);
        Assert.Equal(
            "The Workspace repository https://github.com/elsewhere/unknown.git is not imported into GrayMoon. Import it through Connectors, then import this folder.",
            preview.Error);
        Assert.False(result.Success);
        Assert.Equal(preview.Error, result.Error);
        await fixture.AssertNoWorkspaceAsync();
    }

    [Fact]
    public async Task An_invalid_definition_stops_the_import_with_the_evaluators_message_and_writes_nothing()
    {
        await using var fixture = await NewFixtureAsync();
        await SeedCatalogAsync(fixture);
        fixture.Bridge.Handler = Inspect(RootUrl, "{ \"version\": 1 }");

        var result = await Import(fixture).ImportFromDiskAsync(Folder, DotNet);

        Assert.False(result.Success);
        Assert.StartsWith("The Workspace definition is invalid:", result.Error);
        Assert.DoesNotContain(fixture.Bridge.Sent, s => s.Command == WorkerHubMethods.WriteRepositoryFile);
        Assert.DoesNotContain(fixture.Bridge.Sent, s => s.Command == WorkerHubMethods.AttachWorkspaceRepository);
        await fixture.AssertNoWorkspaceAsync();
    }

    // ---- A folder without a definition --------------------------------------------------------------------------

    [Fact]
    public async Task A_plain_folder_links_the_repositories_gray_moon_knows_with_the_chosen_profile_and_writes_nothing()
    {
        await using var fixture = await NewFixtureAsync();
        var (_, api, web) = await SeedCatalogAsync(fixture);
        fixture.Bridge.Handler = Inspect(
            rootOrigin: null, definition: null, ("Api", ApiUrl), ("Web", "git@github.com:acme/Web.git"), ("Unknown", "https://github.com/x/y.git"), ("NoRemote", null));

        var preview = await Import(fixture).PreflightImportAsync(Folder);
        var result = await Import(fixture).ImportFromDiskAsync(Folder, new ImportWorkspaceOptions(WorkspaceType.Basic, WorkspaceVersioningMode.None, WorkspaceCiProvider.None));

        Assert.True(preview.Success, preview.Error);
        Assert.Equal(ImportFolderShape.PlainFolder, preview.Shape);
        Assert.False(preview.ProfileFromDefinition);
        Assert.Equal(["Api", "Web"], preview.LinkedOnDisk);
        Assert.Contains(preview.NotLinked, line => line == "NoRemote: it has no origin remote");
        Assert.Contains(preview.NotLinked, line => line.StartsWith("Unknown:"));

        Assert.True(result.Success, result.Error);
        await using var db = new AppDbContext(fixture.Seed.Options);
        var workspace = await db.Workspaces.AsNoTracking().SingleAsync();
        Assert.Equal(WorkspaceType.Basic, workspace.Type);
        var links = await fixture.GetLinksAsync(workspace.WorkspaceId);
        Assert.Equal(new[] { api.RepositoryId, web.RepositoryId }.Order(), links.Select(l => l.RepositoryId).Order());
        Assert.All(links, link => Assert.Equal(WorkspaceRepositoryRole.Source, link.Role));

        // Nothing is written into a folder that has no definition of its own.
        Assert.All(fixture.Bridge.Sent, s => Assert.Equal(WorkerHubMethods.InspectWorkspaceFolder, s.Command));
    }

    [Fact]
    public async Task A_git_repository_without_a_definition_is_imported_for_its_inner_repositories_only()
    {
        await using var fixture = await NewFixtureAsync();
        var (root, api, _) = await SeedCatalogAsync(fixture);
        fixture.Bridge.Handler = Inspect(RootUrl, definition: null, ("Api", ApiUrl));

        var preview = await Import(fixture).PreflightImportAsync(Folder);
        var result = await Import(fixture).ImportFromDiskAsync(Folder, DotNet);

        Assert.True(preview.Success, preview.Error);
        Assert.Equal(ImportFolderShape.RepositoryWithoutDefinition, preview.Shape);
        Assert.Equal(WorkspaceRepositoryOperations.RepositoryWithoutDefinitionNote, preview.Note);
        Assert.Null(preview.WorkspaceRepository);

        Assert.True(result.Success, result.Error);
        var link = Assert.Single(await fixture.GetLinksAsync(result.WorkspaceId!.Value));
        Assert.Equal(api.RepositoryId, link.RepositoryId);
        Assert.NotEqual(root.RepositoryId, link.RepositoryId);
        Assert.DoesNotContain(fixture.Bridge.Sent, s => s.Command == WorkerHubMethods.AttachWorkspaceRepository);
    }

    [Fact]
    public async Task A_repository_in_a_folder_with_another_name_is_not_linked_and_the_reason_is_given()
    {
        await using var fixture = await NewFixtureAsync();
        await SeedCatalogAsync(fixture);
        fixture.Bridge.Handler = Inspect(null, null, ("Api", ApiUrl), ("web-copy", WebUrl));

        var preview = await Import(fixture).PreflightImportAsync(Folder);

        Assert.True(preview.Success, preview.Error);
        Assert.Equal(["Api"], preview.LinkedOnDisk);
        Assert.Equal("web-copy: it is Web, which GrayMoon expects in a folder named 'Web'", Assert.Single(preview.NotLinked));
    }

    [Fact]
    public async Task A_folder_with_nothing_gray_moon_knows_cannot_be_imported()
    {
        await using var fixture = await NewFixtureAsync();
        await SeedCatalogAsync(fixture);
        fixture.Bridge.Handler = Inspect(null, null, ("Unknown", "https://github.com/x/y.git"));

        var known = await Import(fixture).PreflightImportAsync(Folder);
        fixture.Bridge.Handler = Inspect(null, null);
        var empty = await Import(fixture).PreflightImportAsync(Folder);

        Assert.False(known.Success);
        Assert.True(known.NeedsConnectors);
        Assert.Equal(WorkspaceRepositoryOperations.NothingKnownMessage, known.Error);
        Assert.Single(known.NotLinked);
        Assert.Equal(WorkspaceRepositoryOperations.NoRepositoriesMessage, empty.Error);
    }

    // ---- Refusals and rollback ----------------------------------------------------------------------------------

    [Fact]
    public async Task A_folder_whose_name_is_a_workspace_already_or_that_is_gone_is_refused()
    {
        await using var fixture = await NewFixtureAsync();
        await SeedCatalogAsync(fixture);
        await fixture.Seed.AddWorkspaceAsync("EXISTING");
        fixture.Bridge.Handler = (_, _) => new WorkerCommandResponse(true, new { exists = false }, null);

        var taken = await Import(fixture).PreflightImportAsync(Folder);
        var gone = await Import(fixture).PreflightImportAsync("other");

        Assert.Equal("A workspace named 'existing' already exists.", taken.Error);
        Assert.Equal("The folder 'other' does not exist under the workspace root.", gone.Error);
    }

    [Fact]
    public async Task Without_the_worker_nothing_is_read_and_nothing_is_created()
    {
        await using var fixture = await NewFixtureAsync();
        fixture.Bridge.IsWorkerConnected = false;

        var list = await Import(fixture).ListImportableFoldersAsync();
        var result = await Import(fixture).ImportFromDiskAsync(Folder, DotNet);

        Assert.NotNull(list.Error);
        Assert.False(result.Success);
        Assert.Empty(fixture.Bridge.Sent);
        await fixture.AssertNoWorkspaceAsync();
    }

    [Fact]
    public async Task An_import_that_fails_after_the_workspace_was_created_removes_it_again_and_leaves_the_folder_alone()
    {
        await using var fixture = await NewFixtureAsync();
        await SeedCatalogAsync(fixture);
        // Another Workspace, with a link, that the rollback must not touch.
        var other = await fixture.Seed.AddWorkspaceAsync("other");
        var bystander = await fixture.Seed.AddRepositoryAsync(await fixture.GetGitHubConnectorAsync(), "Bystander", "https://github.com/acme/Bystander.git");
        await fixture.Seed.LinkAsync(other, bystander, WorkspaceRepositoryRole.Source);

        var inspect = Inspect(RootUrl, Definition(("basic", "none", "none"), ("Api", ApiUrl)), ("Api", ApiUrl));
        fixture.Bridge.Handler = (command, args) => command switch
        {
            // The adopt works and writes hooks; reading the definition afterwards fails.
            "GetFileContents" => new WorkerCommandResponse(true, new { errorMessage = "Access to the path is denied." }, null),
            WorkerHubMethods.DetachRepositories => DetachScript.Answer(command, args),
            _ => inspect(command, args),
        };

        var result = await Import(fixture).ImportFromDiskAsync(Folder, DotNet);

        Assert.False(result.Success);
        Assert.Contains("Access to the path is denied", result.Error);
        Assert.Null(result.RollbackResidue);

        // The hooks the adopt wrote are taken back before the rows go; the folder itself is never discarded.
        var commands = fixture.Bridge.Sent.Select(s => s.Command).ToList();
        Assert.Contains(WorkerHubMethods.DetachRepositories, commands);
        Assert.DoesNotContain(WorkerHubMethods.DiscardWorkspaceRoot, commands);

        await using var db = new AppDbContext(fixture.Seed.Options);
        Assert.Equal(["other"], await db.Workspaces.AsNoTracking().Select(w => w.Name).ToListAsync());
        Assert.Equal(bystander.RepositoryId, Assert.Single(await db.WorkspaceRepositories.AsNoTracking().ToListAsync()).RepositoryId);
    }

    // ---- The dialog's rules -------------------------------------------------------------------------------------

    [Fact]
    public void Import_is_offered_only_for_a_checked_folder_with_a_usable_worker()
    {
        var ok = new ImportWorkspacePreflight { Success = true };

        Assert.True(ImportWorkspaceFlow.CanImport(false, null, "f", ok, false));
        Assert.False(ImportWorkspaceFlow.CanImport(true, null, "f", ok, false));
        Assert.False(ImportWorkspaceFlow.CanImport(false, "Worker not connected.", "f", ok, false));
        Assert.False(ImportWorkspaceFlow.CanImport(false, null, null, ok, false));
        Assert.False(ImportWorkspaceFlow.CanImport(false, null, "f", ok, true));
        Assert.False(ImportWorkspaceFlow.CanImport(false, null, "f", ImportWorkspacePreflight.Failed("no"), false));
        Assert.False(ImportWorkspaceFlow.CanImport(false, null, "f", null, false));
    }

    [Fact]
    public async Task The_sync_runs_only_after_a_successful_import_and_a_failed_sync_does_not_undo_it()
    {
        var failing = new StubImport(ImportWorkspaceResult.Failed("no"));
        var synced = new List<int>();

        var failed = await ImportWorkspaceFlow.RunAsync(failing, "f", DotNet, (id, _, _) => { synced.Add(id); return Task.CompletedTask; }, null, CancellationToken.None);
        var imported = new StubImport(new ImportWorkspaceResult { Success = true, WorkspaceId = 12 });
        var ok = await ImportWorkspaceFlow.RunAsync(imported, "f", DotNet, (id, _, _) => { synced.Add(id); return Task.CompletedTask; }, null, CancellationToken.None);
        var syncFails = await ImportWorkspaceFlow.RunAsync(imported, "f", DotNet, (_, _, _) => throw new InvalidOperationException("fetch failed"), null, CancellationToken.None);

        Assert.False(failed.SyncRequested);
        Assert.True(ok.SyncRequested);
        Assert.Null(ok.SyncError);
        Assert.Equal([12], synced);
        Assert.True(syncFails.Import.Success);
        Assert.Equal("fetch failed", syncFails.SyncError);
        Assert.False(ImportResultPanel.From(ok).HasContent);
        Assert.True(ImportResultPanel.From(syncFails).HasContent);
    }

    private sealed class StubImport(ImportWorkspaceResult result) : IWorkspaceImportOperations
    {
        public Task<ImportableFolderList> ListImportableFoldersAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<ImportWorkspacePreflight> PreflightImportAsync(string folderName, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<ImportWorkspaceResult> ImportFromDiskAsync(string folderName, ImportWorkspaceOptions options, IProgress<OperationProgress>? progress = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(result);
    }

    private static IWorkspaceImportOperations Import(OperationsFixture fixture) => fixture.Operations;

    private static async Task<OperationsFixture> NewFixtureAsync()
    {
        var fixture = await OperationsFixture.CreateAsync();
        await fixture.ConfigureRootAsync();
        return fixture;
    }

    private static async Task<(Repository Root, Repository Api, Repository Web)> SeedCatalogAsync(OperationsFixture fixture)
    {
        var github = await fixture.Seed.AddConnectorAsync("github", ConnectorType.GitHub, "https://api.github.com");
        var root = await fixture.Seed.AddRepositoryAsync(github, "ws-root", RootUrl);
        var api = await fixture.Seed.AddRepositoryAsync(github, "Api", ApiUrl);
        var web = await fixture.Seed.AddRepositoryAsync(github, "Web", WebUrl);
        return (root, api, web);
    }

    /// <summary>The Worker's view of the folder; every other command succeeds, and the definition is also what a read of it returns.</summary>
    private static Func<string, object, WorkerCommandResponse> Inspect(string? rootOrigin, string? definition, params (string Name, string? Origin)[] children) =>
        (command, _) => command switch
        {
            WorkerHubMethods.InspectWorkspaceFolder => new WorkerCommandResponse(true, new
            {
                exists = true,
                rootRepository = rootOrigin is null ? null : new { name = Folder, originUrl = rootOrigin },
                repositories = children.Select(c => new { name = c.Name, originUrl = c.Origin }).ToArray(),
                definitionFound = definition is not null,
                definitionContent = definition,
            }, null),
            "GetFileContents" => new WorkerCommandResponse(true, new { content = definition }, null),
            WorkerHubMethods.AttachWorkspaceRepository => new WorkerCommandResponse(true, new { success = true, adoptedExisting = true, branch = "main" }, null),
            _ => new WorkerCommandResponse(true, new { success = true, written = true, removed = true }, null),
        };

    private static string Definition((string Type, string Versioning, string Ci) profile, params (string Name, string Url)[] repositories) =>
        new WorkspaceManifestFile().Serialize(new WorkspaceManifest(
            1,
            new WorkspaceManifestWorkspace(new WorkspaceManifestProfile(profile.Type, profile.Versioning, profile.Ci)),
            [new WorkspaceManifestConnector("github", "https://github.com")],
            repositories.Select(r => new WorkspaceManifestRepository(r.Name, r.Url, "https://github.com")).ToList()));
}
