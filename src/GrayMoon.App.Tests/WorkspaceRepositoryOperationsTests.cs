using GrayMoon.Abstractions.Worker;
using GrayMoon.Abstractions.Workspaces;
using GrayMoon.App.Data;
using GrayMoon.App.Models;
using GrayMoon.App.Repositories;
using GrayMoon.App.Services;
using GrayMoon.App.Services.Application;
using GrayMoon.App.Services.GitChanges;
using GrayMoon.App.Services.WorkspaceManifest;
using GrayMoon.Application.Features;
using GrayMoon.Application.WorkspaceManifest;
using GrayMoon.Common.Manifest;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using AppWorkspaceRepository = GrayMoon.App.Repositories.WorkspaceRepository;

namespace GrayMoon.App.Tests;

public sealed class WorkspaceRepositoryOperationsTests
{
    private const string MismatchMessage = "The GrayMoon Worker version (1.0.0) does not match this GrayMoon version (2.0.0). Update the Worker.";

    [Fact]
    public async Task Enable_refused_when_worker_version_does_not_match()
    {
        await using var fixture = await OperationsFixture.CreateAsync();
        var (workspace, root) = await fixture.SeedEnableScenarioAsync();
        fixture.Bridge.UnavailableReason = MismatchMessage;

        var result = await fixture.Operations.EnableWorkspaceRepositoryAsync(workspace.WorkspaceId, root.RepositoryId);

        Assert.False(result.Success);
        Assert.Equal(MismatchMessage, result.Error);
        Assert.Empty(fixture.Bridge.Sent);
        Assert.Empty(fixture.GitChangesScans);
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
        Assert.Empty(fixture.GitChangesScans);
        Assert.Empty(await fixture.GetLinksAsync(workspace.WorkspaceId));
    }

    [Fact]
    public async Task Enable_moves_a_source_repository_to_the_workspace_role()
    {
        await using var fixture = await OperationsFixture.CreateAsync();
        var (workspace, root) = await fixture.SeedEnableScenarioAsync();
        await fixture.Seed.LinkAsync(workspace, root, WorkspaceRepositoryRole.Source);
        var sourceLinkId = await MarkSourceStateAsync(fixture, workspace.WorkspaceId);

        var result = await fixture.Operations.EnableWorkspaceRepositoryAsync(workspace.WorkspaceId, root.RepositoryId);

        Assert.True(result.Success, result.Error);
        Assert.Single(fixture.Bridge.Sent, s => s.Command == "AttachWorkspaceRepository");
        Assert.Equal(["gitignore", "manifest"], fixture.Manifest.Calls.ToArray());
        // One link, now the Workspace repository, starting clean: nothing of the Source checkout's state is carried over.
        var link = Assert.Single(await fixture.GetLinksAsync(workspace.WorkspaceId));
        Assert.Equal(WorkspaceRepositoryRole.Workspace, link.Role);
        Assert.Equal(root.RepositoryId, link.RepositoryId);
        Assert.NotEqual(sourceLinkId, link.WorkspaceRepositoryId);
        Assert.False(link.IsReference);
        Assert.Null(link.BranchName);
        // The user is told the earlier folder stays behind.
        Assert.True(result.HasWarnings);
        Assert.Contains("left on disk", result.WarningSummary);
    }

    [Fact]
    public async Task A_failed_attach_leaves_a_source_repository_exactly_as_it_was()
    {
        await using var fixture = await OperationsFixture.CreateAsync();
        var (workspace, root) = await fixture.SeedEnableScenarioAsync();
        await fixture.Seed.LinkAsync(workspace, root, WorkspaceRepositoryRole.Source);
        var sourceLinkId = await MarkSourceStateAsync(fixture, workspace.WorkspaceId);
        fixture.Bridge.Handler = (command, _) => command == "AttachWorkspaceRepository"
            ? new WorkerCommandResponse(true, new { success = false, errorMessage = "Git fetch failed." }, null)
            : new WorkerCommandResponse(true, new { success = true }, null);

        var result = await fixture.Operations.EnableWorkspaceRepositoryAsync(workspace.WorkspaceId, root.RepositoryId);

        Assert.False(result.Success);
        Assert.Equal("Git fetch failed.", result.Error);
        Assert.Empty(fixture.Manifest.Calls);
        // Same row, same role, and the state a delete-and-re-add would have lost is still there.
        var link = Assert.Single(await fixture.GetLinksAsync(workspace.WorkspaceId));
        Assert.Equal(sourceLinkId, link.WorkspaceRepositoryId);
        Assert.Equal(WorkspaceRepositoryRole.Source, link.Role);
        Assert.True(link.IsReference);
        Assert.Equal("feature/in-progress", link.BranchName);
    }

    /// <summary>Gives the seeded Source link state that only a destructive move would lose. Returns its id.</summary>
    private static async Task<int> MarkSourceStateAsync(OperationsFixture fixture, int workspaceId)
    {
        await using var db = new AppDbContext(fixture.Seed.Options);
        var link = await db.WorkspaceRepositories.SingleAsync(l => l.WorkspaceId == workspaceId);
        link.IsReference = true;
        link.BranchName = "feature/in-progress";
        await db.SaveChangesAsync();
        return link.WorkspaceRepositoryId;
    }

    [Fact]
    public async Task Files_kept_in_the_folder_are_reported_as_a_warning_not_a_failure()
    {
        await using var fixture = await OperationsFixture.CreateAsync();
        var (workspace, root) = await fixture.SeedEnableScenarioAsync();
        fixture.Bridge.Handler = (command, _) => command == "AttachWorkspaceRepository"
            ? new WorkerCommandResponse(true, new { success = true, branch = "main", keptFiles = new[] { ".claude/settings.local.json", "CLAUDE.md" }, keptFileCount = 3 }, null)
            : new WorkerCommandResponse(true, new { success = true }, null);

        var result = await fixture.Operations.EnableWorkspaceRepositoryAsync(workspace.WorkspaceId, root.RepositoryId);

        Assert.True(result.Success, result.Error);
        Assert.Equal(["gitignore", "manifest"], fixture.Manifest.Calls.ToArray());
        var warning = Assert.Single(result.Warnings!);
        Assert.Equal(
            "The repository's files were checked out into the Workspace folder, which is now its working copy. 3 files already in the Workspace folder were kept instead of the repository's versions: .claude/settings.local.json, CLAUDE.md and 1 more. They show in Changes.",
            warning);
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
        Assert.Empty(fixture.GitChangesScans);
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
        Assert.Equal([root.RepositoryId], fixture.GitChangesScans);
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
        Assert.False(args.GetProperty("requireEmptyRoot").GetBoolean());
    }

    private const string SetupBranch = "graymoon/workspace-setup";

    /// <summary>A Worker that clones onto <paramref name="attachedBranch"/> and answers CreateBranch with <paramref name="createBranch"/>.</summary>
    private static void ScriptSetupBranchWorker(OperationsFixture fixture, object createBranch, string attachedBranch = "main", bool isUnborn = false, bool adoptedExisting = false) =>
        fixture.Bridge.Handler = (command, _) => command switch
        {
            "AttachWorkspaceRepository" => new WorkerCommandResponse(true, new { success = true, branch = attachedBranch, isUnborn, adoptedExisting }, null),
            "CreateBranch" => new WorkerCommandResponse(true, createBranch, null),
            _ => new WorkerCommandResponse(true, new { success = true }, null),
        };

    [Fact]
    public async Task Enable_with_a_setup_branch_creates_it_from_the_cloned_branch_before_writing_the_files()
    {
        await using var fixture = await OperationsFixture.CreateAsync();
        var (workspace, root) = await fixture.SeedEnableScenarioAsync();
        var order = new List<string>();
        fixture.Bridge.Handler = (command, _) =>
        {
            order.Add(command);
            return command switch
            {
                "AttachWorkspaceRepository" => new WorkerCommandResponse(true, new { success = true, branch = "trunk" }, null),
                "CreateBranch" => new WorkerCommandResponse(true, new { success = true, currentBranch = SetupBranch }, null),
                _ => new WorkerCommandResponse(true, new { success = true }, null),
            };
        };

        var result = await fixture.Operations.EnableWorkspaceRepositoryAsync(workspace.WorkspaceId, root.RepositoryId, setupBranchName: "  " + SetupBranch + " ");

        Assert.True(result.Success, result.Error);
        Assert.Equal(["AttachWorkspaceRepository", "CreateBranch"], order.Take(2).ToArray());
        // The files are written only after the branch exists.
        Assert.Equal(["gitignore", "manifest"], fixture.Manifest.Calls.ToArray());
        var create = Assert.Single(fixture.Bridge.Sent, s => s.Command == "CreateBranch");
        var args = ManifestTestFixture.ToJson(create.Args);
        Assert.Equal(SetupBranch, args.GetProperty("newBranchName").GetString());
        Assert.Equal("trunk", args.GetProperty("baseBranchName").GetString());
        Assert.Equal("ws-root", args.GetProperty("repositoryName").GetString());
        Assert.Equal("ws-root", args.GetProperty("workspaceRepositoryName").GetString());
        Assert.Equal(ManifestTestFixture.WorkspaceFolder, args.GetProperty("workspaceName").GetString());
        Assert.Equal(root.RepositoryId, args.GetProperty("repositoryId").GetInt32());
        // GrayMoon never commits or pushes the branch.
        Assert.DoesNotContain(fixture.Bridge.Sent, s => s.Command.Contains("Commit", StringComparison.OrdinalIgnoreCase) || s.Command.Contains("Push", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("graymoon/workspace-setup")]
    [InlineData("GrayMoon/Workspace-Setup")]
    public async Task Enable_with_a_setup_branch_that_exists_on_the_remote_changes_nothing(string existing)
    {
        await using var fixture = await OperationsFixture.CreateAsync();
        var (workspace, root) = await fixture.SeedEnableScenarioAsync();
        fixture.Remote.BranchList = new("main", ["main", existing], false, null);

        var result = await fixture.Operations.EnableWorkspaceRepositoryAsync(workspace.WorkspaceId, root.RepositoryId, setupBranchName: SetupBranch);

        Assert.False(result.Success);
        Assert.Equal(GrayMoon.Application.Workspaces.WorkspaceSetupBranch.ExistsMessage(SetupBranch), result.Error);
        Assert.Empty(fixture.Bridge.Sent);
        Assert.Empty(fixture.Manifest.Calls);
        Assert.Empty(await fixture.GetLinksAsync(workspace.WorkspaceId));
    }

    [Fact]
    public async Task Enable_with_a_setup_branch_stops_when_the_remote_branches_cannot_be_read()
    {
        await using var fixture = await OperationsFixture.CreateAsync();
        var (workspace, root) = await fixture.SeedEnableScenarioAsync();
        fixture.Remote.BranchList = GrayMoon.Application.Workspaces.RemoteBranchList.Failed("GitHub is unreachable.");

        var result = await fixture.Operations.EnableWorkspaceRepositoryAsync(workspace.WorkspaceId, root.RepositoryId, setupBranchName: SetupBranch);

        Assert.False(result.Success);
        Assert.Contains("GitHub is unreachable.", result.Error);
        Assert.Empty(fixture.Bridge.Sent);
        Assert.Empty(await fixture.GetLinksAsync(workspace.WorkspaceId));
    }

    [Theory]
    [InlineData("-x")]
    [InlineData("a b")]
    [InlineData("a..b")]
    [InlineData("refs/heads/x")]
    public async Task Enable_with_an_invalid_setup_branch_name_changes_nothing(string name)
    {
        await using var fixture = await OperationsFixture.CreateAsync();
        var (workspace, root) = await fixture.SeedEnableScenarioAsync();

        var result = await fixture.Operations.EnableWorkspaceRepositoryAsync(workspace.WorkspaceId, root.RepositoryId, setupBranchName: name);

        Assert.False(result.Success);
        Assert.Equal(GrayMoon.Application.Workspaces.WorkspaceSetupBranch.InvalidMessage(name), result.Error);
        Assert.Equal(0, fixture.Remote.BranchListReads);
        Assert.Empty(fixture.Bridge.Sent);
        Assert.Empty(await fixture.GetLinksAsync(workspace.WorkspaceId));
    }

    [Theory]
    [InlineData(false, "main")]
    [InlineData(true, null)]
    public async Task Enable_writes_no_files_when_the_setup_branch_is_not_checked_out(bool success, string? currentBranch)
    {
        await using var fixture = await OperationsFixture.CreateAsync();
        var (workspace, root) = await fixture.SeedEnableScenarioAsync();
        ScriptSetupBranchWorker(fixture, success
            ? new { success, currentBranch, errorMessage = (string?)null }
            : new { success, currentBranch, errorMessage = (string?)"fatal: cannot lock ref" });

        var result = await fixture.Operations.EnableWorkspaceRepositoryAsync(workspace.WorkspaceId, root.RepositoryId, setupBranchName: SetupBranch);

        Assert.False(result.Success);
        Assert.Contains("were not written", result.Error);
        // Nothing may be written on the default branch.
        Assert.Empty(fixture.Manifest.Calls);
        // The clone stays attached: removing the link would make a retry adopt the folder without a branch.
        Assert.Single(await fixture.GetLinksAsync(workspace.WorkspaceId, WorkspaceRepositoryRole.Workspace));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Enable_creates_no_setup_branch_for_an_empty_repository_or_an_adopted_folder(bool isUnborn, bool adoptedExisting)
    {
        await using var fixture = await OperationsFixture.CreateAsync();
        var (workspace, root) = await fixture.SeedEnableScenarioAsync();
        ScriptSetupBranchWorker(fixture, new { success = true, currentBranch = SetupBranch }, isUnborn: isUnborn, adoptedExisting: adoptedExisting);

        var result = await fixture.Operations.EnableWorkspaceRepositoryAsync(workspace.WorkspaceId, root.RepositoryId, setupBranchName: SetupBranch);

        Assert.True(result.Success, result.Error);
        Assert.DoesNotContain(fixture.Bridge.Sent, s => s.Command == "CreateBranch");
        // An adopted folder keeps its files; an empty repository gets them on its unborn branch as before.
        Assert.Equal(adoptedExisting ? [] : new[] { "gitignore", "manifest" }, fixture.Manifest.Calls.ToArray());
    }

    [Fact]
    public async Task Enable_without_a_setup_branch_asks_for_no_branches_and_creates_none()
    {
        await using var fixture = await OperationsFixture.CreateAsync();
        var (workspace, root) = await fixture.SeedEnableScenarioAsync();

        var result = await fixture.Operations.EnableWorkspaceRepositoryAsync(workspace.WorkspaceId, root.RepositoryId);

        Assert.True(result.Success, result.Error);
        Assert.Equal(0, fixture.Remote.BranchListReads);
        Assert.DoesNotContain(fixture.Bridge.Sent, s => s.Command == "CreateBranch");
    }

    [Theory]
    [InlineData("graymoon/workspace-setup", true)]
    [InlineData("feature/a.b_c-1", true)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("-x", false)]
    [InlineData("a b", false)]
    [InlineData("a..b", false)]
    [InlineData("a@{b", false)]
    [InlineData("@", false)]
    [InlineData("HEAD", false)]
    [InlineData("/a", false)]
    [InlineData("a/", false)]
    [InlineData("a//b", false)]
    [InlineData("a/.b", false)]
    [InlineData("a.lock", false)]
    [InlineData("a.lock/b", false)]
    [InlineData("a.", false)]
    [InlineData("a~1", false)]
    [InlineData("a^", false)]
    [InlineData("a:b", false)]
    [InlineData("a?", false)]
    [InlineData("a*", false)]
    [InlineData("a[b", false)]
    [InlineData("a\\b", false)]
    [InlineData("a\"b", false)]
    [InlineData("a'b", false)]
    [InlineData("refs/heads/a", false)]
    public void Setup_branch_names_follow_the_git_ref_rules(string name, bool valid) =>
        Assert.Equal(valid, GrayMoon.Application.Workspaces.WorkspaceSetupBranch.Validate(name) is null);

    [Theory]
    [InlineData(5, null, null, true)]
    [InlineData(5, 4, null, true)]
    [InlineData(5, 5, null, false)]
    [InlineData(null, 5, null, false)]
    [InlineData(5, null, 5, false)]
    [InlineData(5, null, 6, true)]
    public void The_setup_branch_is_asked_for_only_when_a_new_repository_will_be_set_up(int? selected, int? current, int? folder, bool expected) =>
        Assert.Equal(expected, GrayMoon.App.Components.Modals.WorkspaceModal.NeedsSetupBranch(selected, current, folder));

    [Fact]
    public async Task Enable_leaves_an_existing_workspace_repository_definition_unchanged()
    {
        await using var fixture = await OperationsFixture.CreateAsync();
        var (workspace, root) = await fixture.SeedEnableScenarioAsync();
        fixture.Bridge.Handler = (command, _) => command == "AttachWorkspaceRepository"
            ? new WorkerCommandResponse(true, new { success = true, adoptedExisting = true }, null)
            : new WorkerCommandResponse(true, new { success = true }, null);

        var result = await fixture.Operations.EnableWorkspaceRepositoryAsync(workspace.WorkspaceId, root.RepositoryId);

        Assert.True(result.Success, result.Error);
        Assert.Empty(fixture.Manifest.Calls);
        var link = Assert.Single(await fixture.GetLinksAsync(workspace.WorkspaceId));
        Assert.Equal(WorkspaceRepositoryRole.Workspace, link.Role);
        Assert.Equal(root.RepositoryId, link.RepositoryId);
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

    // ---- Preflight ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Preflight_previews_a_valid_definition_without_touching_the_worker_or_database()
    {
        await using var fixture = await OperationsFixture.CreateAsync();
        var github = await fixture.Seed.AddConnectorAsync("github", ConnectorType.GitHub, "https://api.github.com");
        var root = await fixture.Seed.AddRepositoryAsync(github, "ws-root", "https://github.com/acme/ws-root.git");
        await fixture.Seed.AddRepositoryAsync(github, "Api", "https://github.com/acme/Api.git");
        await fixture.Seed.AddRepositoryAsync(github, "Web", "https://github.com/acme/Web.git");
        fixture.UseDefinition(Definition(
            ("dotNetDependency", "gitVersion", "githubActions"),
            ["https://github.com"],
            ("Api", "https://github.com/acme/Api.git"),
            ("Web", "https://github.com/acme/Web.git")));

        var preflight = await fixture.Operations.PreflightRestoreAsync(root.RepositoryId);

        Assert.True(preflight.Success, preflight.Error);
        Assert.Equal(WorkspaceType.DotNetDependency, preflight.WorkspaceType);
        Assert.Equal(WorkspaceVersioningMode.GitVersion, preflight.VersioningMode);
        Assert.Equal(WorkspaceCiProvider.GitHubActions, preflight.CiProvider);
        Assert.Equal(2, preflight.RepositoryCount);
        Assert.Equal(1, preflight.ConnectorCount);
        Assert.Equal(1, preflight.ConfiguredConnectorCount);
        Assert.Empty(preflight.MissingConnectors);
        Assert.Empty(preflight.MissingRepositories);

        Assert.Equal([root.RepositoryId], fixture.Remote.Reads.ToArray());
        Assert.Empty(fixture.Bridge.Sent);
        Assert.Empty(fixture.FolderBridge.Sent);
        await fixture.AssertNoWorkspaceAsync();
    }

    [Fact]
    public async Task Preflight_fails_when_the_repository_has_no_definition()
    {
        await using var fixture = await OperationsFixture.CreateAsync();
        var root = await fixture.SeedRootAsync();
        fixture.UseDefinition(null);

        var preflight = await fixture.Operations.PreflightRestoreAsync(root.RepositoryId);

        Assert.False(preflight.Success);
        Assert.Equal("This repository does not contain graymoon.json.", preflight.Error);
    }

    [Fact]
    public async Task Preflight_fails_for_invalid_json()
    {
        await using var fixture = await OperationsFixture.CreateAsync();
        var root = await fixture.SeedRootAsync();
        fixture.UseDefinition("{ not json");

        var preflight = await fixture.Operations.PreflightRestoreAsync(root.RepositoryId);

        Assert.False(preflight.Success);
        Assert.StartsWith("The Workspace definition is invalid:", preflight.Error);
    }

    [Fact]
    public async Task Preflight_fails_for_a_newer_schema()
    {
        await using var fixture = await OperationsFixture.CreateAsync();
        var root = await fixture.SeedRootAsync();
        fixture.UseDefinition("{ \"version\": 99, \"workspace\": { \"name\": \"x\" } }");

        var preflight = await fixture.Operations.PreflightRestoreAsync(root.RepositoryId);

        Assert.False(preflight.Success);
        Assert.Equal(
            "This Workspace definition was created by a newer GrayMoon version. Update GrayMoon before restoring it.",
            preflight.Error);
    }

    [Theory]
    [InlineData("pythonMonorepo", "none", "none", "pythonMonorepo")]
    [InlineData("basic", "semver", "none", "semver")]
    [InlineData("basic", "none", "jenkins", "jenkins")]
    public async Task Preflight_fails_for_unknown_profile_values(string type, string versioning, string ci, string unknown)
    {
        await using var fixture = await OperationsFixture.CreateAsync();
        var root = await fixture.SeedRootAsync();
        fixture.UseDefinition(Definition((type, versioning, ci), []));

        var preflight = await fixture.Operations.PreflightRestoreAsync(root.RepositoryId);

        Assert.False(preflight.Success);
        Assert.Equal($"The Workspace definition is invalid: Unknown workspace profile value \"{unknown}\".", preflight.Error);
    }

    [Fact]
    public async Task Preflight_reports_the_read_error_of_the_connector()
    {
        await using var fixture = await OperationsFixture.CreateAsync();
        var root = await fixture.SeedRootAsync();
        fixture.Remote.Error = "Could not read graymoon.json from GitHub: Not authorized.";

        var preflight = await fixture.Operations.PreflightRestoreAsync(root.RepositoryId);

        Assert.False(preflight.Success);
        Assert.Equal("Could not read graymoon.json from GitHub: Not authorized.", preflight.Error);
    }

    [Fact]
    public async Task Preflight_lists_missing_connectors_and_repositories_but_succeeds()
    {
        await using var fixture = await OperationsFixture.CreateAsync();
        var root = await fixture.SeedRootAsync();
        fixture.UseDefinition(Definition(
            ("basic", "none", "none"),
            ["https://github.com", "https://ghe.other.example"],
            ("Missing", "https://ghe.other.example/acme/Missing.git"),
            ("Gone", "https://github.com/acme/Gone.git")));

        var preflight = await fixture.Operations.PreflightRestoreAsync(root.RepositoryId);

        Assert.True(preflight.Success, preflight.Error);
        Assert.Equal(2, preflight.ConnectorCount);
        Assert.Equal(["ghe.other.example"], preflight.MissingConnectors.ToArray());
        Assert.Equal(1, preflight.ConfiguredConnectorCount);
        Assert.Equal(["acme/Gone", "acme/Missing"], preflight.MissingRepositories.ToArray());
    }

    [Fact]
    public async Task Preflight_does_not_count_the_workspace_repository_or_duplicates_as_sources()
    {
        await using var fixture = await OperationsFixture.CreateAsync();
        var root = await fixture.SeedRootAsync();
        var github = await fixture.GetGitHubConnectorAsync();
        await fixture.Seed.AddRepositoryAsync(github, "Api", "https://github.com/acme/Api.git");
        fixture.UseDefinition(Definition(
            ("basic", "none", "none"),
            ["https://github.com"],
            ("ws-root", "git@github.com:acme/ws-root.git"),
            ("Api", "https://github.com/acme/Api.git"),
            ("Api", "https://GitHub.com/acme/Api")));

        var preflight = await fixture.Operations.PreflightRestoreAsync(root.RepositoryId);

        Assert.True(preflight.Success, preflight.Error);
        Assert.Equal(1, preflight.RepositoryCount);
        Assert.Empty(preflight.MissingRepositories);
    }

    // ---- Restore: success ----------------------------------------------------------------------------------------

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

        fixture.UseDefinition(Definition(
            ("dotNetDependency", "gitVersion", "githubActions"),
            ["https://github.com/"],
            ("Api", "git@github.com:acme/Api.git"),
            ("Web", "https://github.com/acme/Web.git")));

        var result = await fixture.Operations.RestoreFromRepositoryAsync(root.RepositoryId, "restored");

        Assert.True(result.Success, result.Error);
        Assert.Null(result.Error);
        Assert.Null(result.Warning);
        Assert.NotNull(result.WorkspaceId);
        Assert.Empty(result.UnresolvedConnectors);
        Assert.Empty(result.UnresolvedRepositories);

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

        // The file used non-canonical URLs, everything resolved: the definition is rewritten in canonical form.
        Assert.Equal(["gitignore", "manifest"], fixture.Manifest.Calls.ToArray());
    }

    [Fact]
    public async Task Preflight_reads_the_definition_at_the_selected_branch()
    {
        await using var fixture = await OperationsFixture.CreateAsync();
        var root = await fixture.SeedRootAsync();
        fixture.UseDefinition(Definition(("basic", "none", "none"), ["https://github.com"]));

        var onBranch = await fixture.Operations.PreflightRestoreAsync(root.RepositoryId, default, "graymoon/workspace-setup");
        var onDefault = await fixture.Operations.PreflightRestoreAsync(root.RepositoryId, default, "  ");

        Assert.True(onBranch.Success, onBranch.Error);
        Assert.True(onDefault.Success, onDefault.Error);
        Assert.Equal(["graymoon/workspace-setup", null], fixture.Remote.Branches.ToArray());
    }

    [Fact]
    public async Task Restore_validates_and_attaches_the_same_branch()
    {
        await using var fixture = await OperationsFixture.CreateAsync();
        var root = await fixture.SeedRootAsync();
        fixture.UseDefinition(Definition(("basic", "none", "none"), ["https://github.com"]));

        var result = await fixture.Operations.RestoreFromRepositoryAsync(
            root.RepositoryId, "branch-restore", branchName: "graymoon/workspace-setup");

        Assert.True(result.Success, result.Error);
        Assert.Equal(["graymoon/workspace-setup"], fixture.Remote.Branches.ToArray());
        var attach = Assert.Single(fixture.Bridge.Sent, s => s.Command == "AttachWorkspaceRepository");
        Assert.Equal("graymoon/workspace-setup", ManifestTestFixture.ToJson(attach.Args).GetProperty("branchName").GetString());
    }

    [Fact]
    public async Task Restore_without_a_branch_keeps_the_default_branch_behavior()
    {
        await using var fixture = await OperationsFixture.CreateAsync();
        var root = await fixture.SeedRootAsync();
        fixture.UseDefinition(Definition(("basic", "none", "none"), ["https://github.com"]));

        var result = await fixture.Operations.RestoreFromRepositoryAsync(root.RepositoryId, "default-restore");

        Assert.True(result.Success, result.Error);
        var attach = Assert.Single(fixture.Bridge.Sent, s => s.Command == "AttachWorkspaceRepository");
        Assert.Equal(System.Text.Json.JsonValueKind.Null, ManifestTestFixture.ToJson(attach.Args).GetProperty("branchName").ValueKind);
    }

    [Fact]
    public void Branch_order_puts_the_default_first_then_sorts_ignoring_case_without_duplicates()
    {
        var ordered = GitHubRemoteWorkspaceManifestReader.OrderBranches(
            "trunk", ["zeta", "Alpha", "trunk", "beta", "Alpha"]);

        Assert.Equal(["trunk", "Alpha", "beta", "zeta"], ordered);
    }

    [Fact]
    public async Task Restore_keeps_a_canonical_definition_untouched()
    {
        await using var fixture = await OperationsFixture.CreateAsync();
        var root = await fixture.SeedRootAsync();
        var github = await fixture.GetGitHubConnectorAsync();
        await fixture.Seed.AddRepositoryAsync(github, "Api", "https://github.com/acme/Api.git");
        fixture.UseDefinition(Definition(("basic", "none", "none"), ["https://github.com"], ("Api", "https://github.com/acme/Api.git")));

        var result = await fixture.Operations.RestoreFromRepositoryAsync(root.RepositoryId, "a-local-name");

        Assert.True(result.Success, result.Error);
        Assert.Equal(["gitignore"], fixture.Manifest.Calls.ToArray());
    }

    [Fact]
    public async Task Restore_does_not_rewrite_a_definition_only_to_drop_a_leftover_name()
    {
        await using var fixture = await OperationsFixture.CreateAsync();
        var root = await fixture.SeedRootAsync();
        var github = await fixture.GetGitHubConnectorAsync();
        await fixture.Seed.AddRepositoryAsync(github, "Api", "https://github.com/acme/Api.git");
        var canonical = Definition(("basic", "none", "none"), ["https://github.com"], ("Api", "https://github.com/acme/Api.git"));
        var withName = canonical.Replace(
            "\"workspace\": {",
            "\"workspace\": {\n    \"name\": \"OtherMachine\",",
            StringComparison.Ordinal);
        fixture.UseDefinition(withName);

        var result = await fixture.Operations.RestoreFromRepositoryAsync(root.RepositoryId, "a-local-name");

        Assert.True(result.Success, result.Error);
        Assert.Equal(["gitignore"], fixture.Manifest.Calls.ToArray());
    }

    [Fact]
    public async Task Restore_with_missing_items_succeeds_reports_them_and_never_rewrites_the_definition()
    {
        await using var fixture = await OperationsFixture.CreateAsync();
        var root = await fixture.SeedRootAsync();
        fixture.UseDefinition(Definition(
            ("basic", "none", "none"),
            ["https://github.com", "https://ghe.other.example"],
            ("Missing", "https://ghe.other.example/acme/Missing.git")));

        var result = await fixture.Operations.RestoreFromRepositoryAsync(root.RepositoryId, "restored");

        Assert.True(result.Success, result.Error);
        Assert.Equal(["ghe.other.example"], result.UnresolvedConnectors.ToArray());
        Assert.Equal(["acme/Missing"], result.UnresolvedRepositories.ToArray());

        var only = Assert.Single(await fixture.GetLinksAsync(result.WorkspaceId!.Value));
        Assert.Equal(WorkspaceRepositoryRole.Workspace, only.Role);

        // Rewriting from the database would drop the missing repository from graymoon.json.
        Assert.Equal(["gitignore"], fixture.Manifest.Calls.ToArray());
    }

    [Fact]
    public async Task Restore_into_an_existing_empty_folder_is_allowed()
    {
        await using var fixture = await OperationsFixture.CreateAsync();
        var root = await fixture.SeedRootAsync();
        fixture.UseDefinition(Definition(("basic", "none", "none"), []));
        fixture.FolderIs(exists: true, isEmpty: true);

        var result = await fixture.Operations.RestoreFromRepositoryAsync(root.RepositoryId, "restored");

        Assert.True(result.Success, result.Error);
    }

    [Fact]
    public async Task Restore_reports_progress_in_user_facing_phases()
    {
        await using var fixture = await OperationsFixture.CreateAsync();
        var root = await fixture.SeedRootAsync();
        var github = await fixture.GetGitHubConnectorAsync();
        await fixture.Seed.AddRepositoryAsync(github, "Api", "https://github.com/acme/Api.git");
        fixture.UseDefinition(Definition(("basic", "none", "none"), ["https://github.com"], ("Api", "https://github.com/acme/Api.git")));
        var messages = new List<string>();

        var result = await fixture.Operations.RestoreFromRepositoryAsync(
            root.RepositoryId, "restored", new SynchronousProgress(p => messages.Add(p.Message)));

        Assert.True(result.Success, result.Error);
        Assert.Equal(
            [
                "Checking Workspace definition...",
                "Preparing Workspace...",
                "Cloning Workspace repository...",
                "Applying Workspace profile...",
                "Linking 1 repositories...",
                "Preparing Workspace files...",
            ],
            messages.ToArray());
    }

    // ---- Restore: refused before anything is created --------------------------------------------------------------

    [Fact]
    public async Task Restore_with_a_missing_definition_creates_nothing()
    {
        await using var fixture = await OperationsFixture.CreateAsync();
        var root = await fixture.SeedRootAsync();
        fixture.UseDefinition(null);

        var result = await fixture.Operations.RestoreFromRepositoryAsync(root.RepositoryId, "restored");

        Assert.False(result.Success);
        Assert.Equal("This repository does not contain graymoon.json.", result.Error);
        Assert.Null(result.WorkspaceId);
        await fixture.AssertNoWorkspaceAsync();
        Assert.Empty(fixture.Bridge.Sent);
        Assert.Empty(fixture.FolderBridge.Sent);
    }

    [Fact]
    public async Task Restore_with_an_invalid_definition_creates_nothing_and_never_attaches()
    {
        await using var fixture = await OperationsFixture.CreateAsync();
        var root = await fixture.SeedRootAsync();
        fixture.UseDefinition(Definition(("someday", "none", "none"), []));

        var result = await fixture.Operations.RestoreFromRepositoryAsync(root.RepositoryId, "restored");

        Assert.False(result.Success);
        Assert.StartsWith("The Workspace definition is invalid:", result.Error);
        await fixture.AssertNoWorkspaceAsync();
        Assert.DoesNotContain(fixture.Bridge.Sent, s => s.Command == "AttachWorkspaceRepository");
        Assert.Empty(fixture.Manifest.Calls);
    }

    [Fact]
    public async Task Restore_into_a_non_empty_folder_changes_nothing()
    {
        await using var fixture = await OperationsFixture.CreateAsync();
        var root = await fixture.SeedRootAsync();
        fixture.UseDefinition(Definition(("basic", "none", "none"), []));
        fixture.FolderIs(exists: true, isEmpty: false);

        var result = await fixture.Operations.RestoreFromRepositoryAsync(root.RepositoryId, "restored");

        Assert.False(result.Success);
        Assert.Equal(WorkspaceRepositoryOperations.NonEmptyFolderMessage, result.Error);
        await fixture.AssertNoWorkspaceAsync();
        Assert.Empty(fixture.Bridge.Sent);
        Assert.DoesNotContain(fixture.FolderBridge.Sent, s => s.Command == "EnsureWorkspace");
    }

    [Fact]
    public async Task Restore_with_a_version_mismatched_worker_does_not_start()
    {
        await using var fixture = await OperationsFixture.CreateAsync();
        var root = await fixture.SeedRootAsync();
        fixture.UseDefinition(Definition(("basic", "none", "none"), []));
        fixture.Bridge.UnavailableReason = MismatchMessage;

        var result = await fixture.Operations.RestoreFromRepositoryAsync(root.RepositoryId, "restored");

        Assert.False(result.Success);
        Assert.Equal(MismatchMessage, result.Error);
        await fixture.AssertNoWorkspaceAsync();
        Assert.Empty(fixture.Bridge.Sent);
        Assert.Empty(fixture.Remote.Reads);
    }

    // ---- Restore: rolled back after the Workspace was created -------------------------------------------------------

    [Fact]
    public async Task Restore_rolls_back_when_the_clone_fails()
    {
        await using var fixture = await OperationsFixture.CreateAsync();
        var root = await fixture.SeedRootAsync();
        fixture.UseDefinition(Definition(("basic", "none", "none"), []));
        fixture.Bridge.Handler = (command, _) => command == "AttachWorkspaceRepository"
            ? new WorkerCommandResponse(true, new { success = false, errorMessage = "Git clone failed." }, null)
            : new WorkerCommandResponse(true, new { success = true, removed = true }, null);

        var result = await fixture.Operations.RestoreFromRepositoryAsync(root.RepositoryId, "restored");

        Assert.False(result.Success);
        Assert.Equal("Git clone failed.", result.Error);
        Assert.Null(result.CleanupResidue);
        var attach = Assert.Single(fixture.Bridge.Sent, s => s.Command == "AttachWorkspaceRepository");
        Assert.True(ManifestTestFixture.ToJson(attach.Args).GetProperty("requireEmptyRoot").GetBoolean());

        var discard = Assert.Single(fixture.Bridge.Sent, s => s.Command == WorkerHubMethods.DiscardWorkspaceRoot);
        var discardArgs = ManifestTestFixture.ToJson(discard.Args);
        Assert.Equal(root.CloneUrl, discardArgs.GetProperty("cloneUrl").GetString());
        Assert.False(discardArgs.GetProperty("keepFolder").GetBoolean());
        await fixture.AssertNoWorkspaceAsync();
    }

    [Fact]
    public async Task Restore_rollback_keeps_a_folder_that_existed_before()
    {
        await using var fixture = await OperationsFixture.CreateAsync();
        var root = await fixture.SeedRootAsync();
        fixture.UseDefinition(Definition(("basic", "none", "none"), []));
        fixture.FolderIs(exists: true, isEmpty: true);
        fixture.Bridge.Handler = (command, _) => command == "AttachWorkspaceRepository"
            ? new WorkerCommandResponse(true, new { success = false, errorMessage = "Git clone failed." }, null)
            : new WorkerCommandResponse(true, new { success = true, removed = true }, null);

        await fixture.Operations.RestoreFromRepositoryAsync(root.RepositoryId, "restored");

        var discard = Assert.Single(fixture.Bridge.Sent, s => s.Command == WorkerHubMethods.DiscardWorkspaceRoot);
        Assert.True(ManifestTestFixture.ToJson(discard.Args).GetProperty("keepFolder").GetBoolean());
    }

    [Fact]
    public async Task Restore_rolls_back_when_the_cloned_definition_is_invalid()
    {
        await using var fixture = await OperationsFixture.CreateAsync();
        var root = await fixture.SeedRootAsync();
        fixture.UseDefinition(Definition(("basic", "none", "none"), []));
        // The branch changed between the preflight and the clone.
        fixture.Bridge.RespondWithFile("{ \"version\": 1 }");

        var result = await fixture.Operations.RestoreFromRepositoryAsync(root.RepositoryId, "restored");

        Assert.False(result.Success);
        Assert.StartsWith("The Workspace definition is invalid:", result.Error);
        Assert.Contains(fixture.Bridge.Sent, s => s.Command == WorkerHubMethods.DiscardWorkspaceRoot);
        await fixture.AssertNoWorkspaceAsync();
        Assert.Empty(fixture.Manifest.Calls);
    }

    [Fact]
    public async Task Restore_rolls_back_when_the_cloned_definition_is_missing()
    {
        await using var fixture = await OperationsFixture.CreateAsync();
        var root = await fixture.SeedRootAsync();
        fixture.UseDefinition(Definition(("basic", "none", "none"), []));
        fixture.Bridge.Handler = (command, _) => command == "GetFileContents"
            ? new WorkerCommandResponse(true, new { errorMessage = "File not found: graymoon.json" }, null)
            : new WorkerCommandResponse(true, new { success = true, removed = true }, null);

        var result = await fixture.Operations.RestoreFromRepositoryAsync(root.RepositoryId, "restored");

        Assert.False(result.Success);
        Assert.Equal("This repository does not contain graymoon.json.", result.Error);
        await fixture.AssertNoWorkspaceAsync();
    }

    [Fact]
    public async Task Restore_rolls_back_when_a_step_after_the_clone_throws()
    {
        await using var fixture = await OperationsFixture.CreateAsync();
        var root = await fixture.SeedRootAsync();
        fixture.UseDefinition(Definition(("basic", "none", "none"), []));
        fixture.Bridge.Handler = (command, _) => command == "GetFileContents"
            ? throw new InvalidOperationException("The Worker connection dropped.")
            : new WorkerCommandResponse(true, new { success = true, removed = true }, null);

        var result = await fixture.Operations.RestoreFromRepositoryAsync(root.RepositoryId, "restored");

        Assert.False(result.Success);
        Assert.Equal("The Worker connection dropped.", result.Error);
        Assert.Contains(fixture.Bridge.Sent, s => s.Command == WorkerHubMethods.DiscardWorkspaceRoot);
        await fixture.AssertNoWorkspaceAsync();
    }

    [Fact]
    public async Task Restore_rolls_back_when_cancelled_after_the_clone()
    {
        await using var fixture = await OperationsFixture.CreateAsync();
        var root = await fixture.SeedRootAsync();
        fixture.UseDefinition(Definition(("basic", "none", "none"), []));
        fixture.Bridge.Handler = (command, _) => command == "GetFileContents"
            ? throw new OperationCanceledException()
            : new WorkerCommandResponse(true, new { success = true, removed = true }, null);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => fixture.Operations.RestoreFromRepositoryAsync(root.RepositoryId, "restored"));

        Assert.Contains(fixture.Bridge.Sent, s => s.Command == WorkerHubMethods.DiscardWorkspaceRoot);
        await fixture.AssertNoWorkspaceAsync();
    }

    [Fact]
    public async Task Restore_rollback_reports_a_folder_it_could_not_prove_it_owns()
    {
        await using var fixture = await OperationsFixture.CreateAsync();
        var root = await fixture.SeedRootAsync();
        fixture.UseDefinition(Definition(("basic", "none", "none"), []));
        fixture.Bridge.Handler = (command, _) => command switch
        {
            "AttachWorkspaceRepository" => new WorkerCommandResponse(true, new { success = false, errorMessage = "Git clone failed." }, null),
            WorkerHubMethods.DiscardWorkspaceRoot => new WorkerCommandResponse(true, new { removed = false, reason = "The folder has changes that GrayMoon did not make." }, null),
            _ => new WorkerCommandResponse(true, new { success = true }, null),
        };

        var result = await fixture.Operations.RestoreFromRepositoryAsync(root.RepositoryId, "restored");

        Assert.False(result.Success);
        Assert.Equal("Git clone failed.", result.Error);
        Assert.Contains("was left in place: The folder has changes that GrayMoon did not make.", result.CleanupResidue);
        await fixture.AssertNoWorkspaceAsync();
    }

    // ---- Restore: written after the Workspace is usable (warnings, no rollback) -----------------------------------

    [Fact]
    public async Task Restore_keeps_the_workspace_with_a_warning_when_gitignore_cannot_be_written()
    {
        await using var fixture = await OperationsFixture.CreateAsync();
        var root = await fixture.SeedRootAsync();
        fixture.UseDefinition(Definition(("basic", "none", "none"), []));
        fixture.Manifest.GitIgnoreResult = GrayMoon.Application.OperationResult.Fail("disk full");

        var result = await fixture.Operations.RestoreFromRepositoryAsync(root.RepositoryId, "restored");

        Assert.True(result.Success, result.Error);
        Assert.Equal("Workspace restored, but .gitignore could not be updated: disk full", result.Warning);
        Assert.Single(await fixture.GetLinksAsync(result.WorkspaceId!.Value));
        Assert.DoesNotContain(fixture.Bridge.Sent, s => s.Command == WorkerHubMethods.DiscardWorkspaceRoot);
    }

    [Fact]
    public async Task Restore_keeps_the_workspace_with_a_warning_when_the_definition_cannot_be_rewritten()
    {
        await using var fixture = await OperationsFixture.CreateAsync();
        var root = await fixture.SeedRootAsync();
        var github = await fixture.GetGitHubConnectorAsync();
        await fixture.Seed.AddRepositoryAsync(github, "Api", "https://github.com/acme/Api.git");
        // Non-canonical URL, so the definition must be rewritten.
        fixture.UseDefinition(Definition(("basic", "none", "none"), ["https://github.com"], ("Api", "git@github.com:acme/Api.git")));
        fixture.Manifest.ManifestResult = GrayMoon.Application.OperationResult.Fail("read-only file");

        var result = await fixture.Operations.RestoreFromRepositoryAsync(root.RepositoryId, "restored");

        Assert.True(result.Success, result.Error);
        Assert.Equal("Workspace restored, but the Workspace definition could not be written: read-only file", result.Warning);
        Assert.Equal(2, (await fixture.GetLinksAsync(result.WorkspaceId!.Value)).Count);
        Assert.DoesNotContain(fixture.Bridge.Sent, s => s.Command == WorkerHubMethods.DiscardWorkspaceRoot);
    }

    internal static string Definition(
        (string Type, string Versioning, string Ci) profile,
        string[] connectors,
        params (string Name, string Url)[] repositories) =>
        new WorkspaceManifestFile().Serialize(new WorkspaceManifest(
            1,
            new WorkspaceManifestWorkspace(new WorkspaceManifestProfile(profile.Type, profile.Versioning, profile.Ci)),
            connectors.Select(c => new WorkspaceManifestConnector("github", c)).ToList(),
            repositories.Select(r => new WorkspaceManifestRepository(r.Name, r.Url, "https://github.com")).ToList()));

    private sealed class SynchronousProgress(Action<GrayMoon.Application.OperationProgress> report)
        : IProgress<GrayMoon.Application.OperationProgress>
    {
        public void Report(GrayMoon.Application.OperationProgress value) => report(value);
    }
}

internal sealed class OperationsFixture : IAsyncDisposable
{
    private readonly ManifestTestFixture _seed;

    public ManifestTestFixture Seed => _seed;

    public ScriptedWorkerBridge Bridge => _seed.Bridge;

    /// <summary>The bridge WorkspaceService uses (folder checks); kept apart from <see cref="Bridge"/>.</summary>
    public ScriptedWorkerBridge FolderBridge { get; } = new();

    public FakeRemoteManifestReader Remote { get; } = new();

    public RecordingManifestService Manifest { get; }

    public List<int> GitChangesScans { get; } = [];

    public GrayMoon.App.Services.GitChanges.WorkspaceGitChangesMonitoringPause MonitoringPause { get; } = new();

    /// <summary>The Workspace lock the operations run under, so a test can hold it.</summary>
    public WorkspaceOperationRunner Lock { get; } = new(NullLogger<WorkspaceOperationRunner>.Instance);

    /// <summary>Stores the workspace root the Import operations read (the fixture's folder is C:\work\ws).</summary>
    public async Task ConfigureRootAsync()
    {
        await using var db = new AppDbContext(_seed.Options);
        await new AppSettingRepository(db).SetValueAsync(AppSettingRepository.WorkspaceRootPathKey, ManifestTestFixture.WorkspaceRoot);
    }

    public WorkspaceRepositoryOperations Operations { get; }

    private OperationsFixture(ManifestTestFixture seed)
    {
        _seed = seed;
        Manifest = new RecordingManifestService(seed.CreateService());

        var workspaceService = new WorkspaceService(
            FolderBridge,
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
            Remote,
            Remote,
            new WorkspaceManifestFile(),
            Manifest,
            Lock,
            new FakeWorkspacePathResolver(seed.Factory),
            new FakeFeatureContextResolver(),
            workspaceRepository,
            workspaceService,
            new RecordingGitChangesScanner(GitChangesScans),
            new WorkspaceRepositoryDetacher(
                seed.Factory,
                seed.Bridge,
                new FakeWorkspacePathResolver(seed.Factory),
                new FakeFeatureContextResolver(),
                MonitoringPause,
                NullLogger<WorkspaceRepositoryDetacher>.Instance),
            NullLogger<WorkspaceRepositoryOperations>.Instance);
    }

    public static async Task<OperationsFixture> CreateAsync() => new(await ManifestTestFixture.CreateAsync());

    /// <summary>The same definition through the connector (preflight) and in the cloned root (Worker read).</summary>
    public void UseDefinition(string? content)
    {
        Remote.Content = content;
        if (content is null)
        {
            Bridge.Handler = (command, _) => command == "GetFileContents"
                ? new WorkerCommandResponse(true, new { errorMessage = "File not found: graymoon.json" }, null)
                : new WorkerCommandResponse(true, new { success = true, removed = true }, null);
        }
        else
        {
            Bridge.RespondWithFile(content);
        }
    }

    public void FolderIs(bool exists, bool isEmpty) =>
        FolderBridge.Handler = (command, _) => command == "GetWorkspaceExists"
            ? new WorkerCommandResponse(true, new { exists, isEmpty }, null)
            : new WorkerCommandResponse(true, new { success = true }, null);

    /// <summary>A GitHub connector plus the Workspace repository "acme/ws-root".</summary>
    public async Task<Repository> SeedRootAsync()
    {
        var github = await _seed.AddConnectorAsync("github", ConnectorType.GitHub, "https://api.github.com");
        return await _seed.AddRepositoryAsync(github, "ws-root", "https://github.com/acme/ws-root.git");
    }

    public async Task<Connector> GetGitHubConnectorAsync()
    {
        await using var db = new AppDbContext(_seed.Options);
        return await db.Connectors.AsNoTracking().FirstAsync(c => c.ConnectorType == ConnectorType.GitHub);
    }

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

    public async Task AssertNoWorkspaceAsync()
    {
        await using var db = new AppDbContext(_seed.Options);
        Assert.Empty(await db.Workspaces.AsNoTracking().ToListAsync());
        Assert.Empty(await db.WorkspaceRepositories.AsNoTracking().ToListAsync());
    }

    public ValueTask DisposeAsync() => _seed.DisposeAsync();
}

/// <summary>Stands in for reading graymoon.json through the GitHub connector.</summary>
internal sealed class FakeRemoteManifestReader : IRemoteWorkspaceManifestReader, GrayMoon.Application.Workspaces.IRemoteRepositoryBranchReader
{
    /// <summary>What the connector lists as the remote branches.</summary>
    public GrayMoon.Application.Workspaces.RemoteBranchList BranchList { get; set; } = new("main", ["main"], false, null);

    public int BranchListReads { get; private set; }

    public Task<GrayMoon.Application.Workspaces.RemoteBranchList> ListAsync(int repositoryId, CancellationToken cancellationToken = default)
    {
        BranchListReads++;
        return Task.FromResult(BranchList);
    }

    /// <summary>File content; null means the repository has no definition.</summary>
    public string? Content { get; set; }

    public string? Error { get; set; }

    public List<int> Reads { get; } = [];

    public List<string?> Branches { get; } = [];

    public Task<RemoteManifestRead> ReadAsync(int repositoryId, string? branchName, CancellationToken cancellationToken = default)
    {
        Reads.Add(repositoryId);
        Branches.Add(branchName);
        if (Error is not null)
            return Task.FromResult(new RemoteManifestRead(false, null, Error));
        return Task.FromResult(Content is null
            ? new RemoteManifestRead(false, null, null)
            : new RemoteManifestRead(true, Content, null));
    }
}

/// <summary>
/// Manifest service stand-in that records the order of the write calls (with injectable results), delegates parsing,
/// and builds the database definition with the real service.
/// </summary>
internal sealed class RecordingManifestService(IWorkspaceManifestService? inner = null) : IWorkspaceManifestService
{
    public List<string> Calls { get; } = [];

    public GrayMoon.Application.OperationResult GitIgnoreResult { get; set; } = GrayMoon.Application.OperationResult.Ok();

    public GrayMoon.Application.OperationResult ManifestResult { get; set; } = GrayMoon.Application.OperationResult.Ok();

    public Task<WorkspaceManifest> BuildFromDatabaseAsync(int workspaceId, CancellationToken cancellationToken = default) =>
        inner?.BuildFromDatabaseAsync(workspaceId, cancellationToken) ?? throw new NotSupportedException();

    public Task<GrayMoon.Application.OperationResult> WriteAuthoritativeManifestAsync(int workspaceId, CancellationToken cancellationToken = default)
    {
        Calls.Add("manifest");
        return Task.FromResult(ManifestResult);
    }

    public Task<GrayMoon.Application.OperationResult> WriteManagedGitIgnoreAsync(int workspaceId, CancellationToken cancellationToken = default)
    {
        Calls.Add("gitignore");
        return Task.FromResult(GitIgnoreResult);
    }

    public Task<GrayMoon.Application.OperationResult> SetRepositoryTagPinsAsync(
        int workspaceId,
        IReadOnlyList<GrayMoon.Application.WorkspaceManifest.WorkspaceRepositoryTagPinChange> changes,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<GrayMoon.Application.OperationResult> SetRepositoryReferenceRevisionsAsync(
        int workspaceId,
        IReadOnlyList<GrayMoon.Application.WorkspaceManifest.WorkspaceRepositoryReferenceRevisionChange> changes,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<WorkspaceManifestDrift> DetectDriftAsync(int workspaceId, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<GrayMoon.Application.OperationResult> WriteVersionFilesAsync(int workspaceId, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<IReadOnlyList<string>> MergeVersionFilesIntoDatabaseAsync(
        int workspaceId,
        IReadOnlyList<WorkspaceManifestVersionFile>? versionFiles,
        CancellationToken cancellationToken = default) =>
        inner?.MergeVersionFilesIntoDatabaseAsync(workspaceId, versionFiles, cancellationToken)
        ?? Task.FromResult<IReadOnlyList<string>>([]);

    public Task<GrayMoon.Application.OperationResult> MergeVersionFilesFromDefinitionAsync(int workspaceId, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public bool CodeGraphEnabled { get; set; }

    public Task<bool> IsCodeGraphEnabledAsync(int workspaceId, CancellationToken cancellationToken = default) =>
        Task.FromResult(CodeGraphEnabled);

    public Task<bool?> GetCodeGraphSwitchAsync(int workspaceId, CancellationToken cancellationToken = default) =>
        Task.FromResult<bool?>(CodeGraphEnabled);

    public Task<GrayMoon.Application.OperationResult> SetCodeGraphEnabledAsync(int workspaceId, bool enabled, CancellationToken cancellationToken = default)
    {
        Calls.Add(enabled ? "codegraph-on" : "codegraph-off");
        CodeGraphEnabled = enabled;
        return Task.FromResult(GrayMoon.Application.OperationResult.Ok());
    }

    public Task<(GrayMoon.Common.Manifest.WorkspaceManifestAi? Ai, string? Error)> ReadAiAsync(int workspaceId, CancellationToken cancellationToken = default) =>
        inner?.ReadAiAsync(workspaceId, cancellationToken) ?? Task.FromResult<(GrayMoon.Common.Manifest.WorkspaceManifestAi?, string?)>((null, null));

    public Task<GrayMoon.Application.OperationResult> WriteCodeGraphConfigAsync(
        int workspaceId,
        WorkspaceFeatureContextId? contextId = null,
        CancellationToken cancellationToken = default)
    {
        Calls.Add("codegraph");
        return Task.FromResult(GrayMoon.Application.OperationResult.Ok());
    }
}

internal sealed class RecordingGitChangesScanner(List<int> repositoryIds) : IGitChangesWorkspaceScanner
{
    public Task ScanWorkspaceAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        CancellationToken cancellationToken,
        Action<GitChangesWorkspaceScanProgress>? onProgress = null,
        bool includeLineStats = false,
        int? repositoryId = null,
        bool persistImmediately = false)
    {
        if (repositoryId is int id)
            repositoryIds.Add(id);
        return Task.CompletedTask;
    }
}
