using GrayMoon.Abstractions.Workspaces;
using GrayMoon.App.Components.Modals;
using GrayMoon.App.Data;
using GrayMoon.App.Models;
using GrayMoon.App.Services.Workspaces;
using GrayMoon.Application;
using GrayMoon.Application.Workspaces;
using Microsoft.EntityFrameworkCore;

namespace GrayMoon.App.Tests;

/// <summary>
/// What the "Restore Workspace" dialog adds on top of the operations (covered by WorkspaceRepositoryOperationsTests):
/// repository choices, name following, folder verdicts, when Restore is enabled, stale preflight answers, the Sync
/// that follows a successful restore and what the result panel says.
/// </summary>
public sealed class RestoreWorkspaceFlowTests
{
    [Fact]
    public async Task Restore_creates_workspace_links_and_profile_then_syncs()
    {
        await using var fixture = await OperationsFixture.CreateAsync();
        var root = await fixture.SeedRootAsync();
        var github = await fixture.GetGitHubConnectorAsync();
        var api = await fixture.Seed.AddRepositoryAsync(github, "Api", "https://github.com/acme/Api.git");
        fixture.UseDefinition(WorkspaceRepositoryOperationsTests.Definition(
            ("dotNetDependency", "gitVersion", "none"), ["https://github.com"], ("Api", "https://github.com/acme/Api.git")));

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

        // Clean success: nothing to show, the dialog opens the Workspace directly.
        Assert.False(RestoreResultPanel.From(outcome).HasContent);
    }

    [Fact]
    public async Task Restore_without_definition_fails_creates_nothing_and_never_syncs()
    {
        await using var fixture = await OperationsFixture.CreateAsync();
        var root = await fixture.SeedRootAsync();
        fixture.UseDefinition(null);

        var sync = new SyncRecorder(fixture);
        var outcome = await RestoreWorkspaceFlow.RunAsync(
            fixture.Operations, root.RepositoryId, "bare", sync.RequestAsync, progress: null, CancellationToken.None);

        Assert.False(outcome.Restore.Success);
        Assert.Equal("This repository does not contain .graymoon.json.", outcome.Restore.Error);
        Assert.False(outcome.SyncRequested);
        Assert.Empty(sync.WorkspaceIds);
        await fixture.AssertNoWorkspaceAsync();
    }

    [Fact]
    public async Task Restore_with_unresolved_repositories_succeeds_and_keeps_the_result_panel_open()
    {
        await using var fixture = await OperationsFixture.CreateAsync();
        var root = await fixture.SeedRootAsync();
        fixture.UseDefinition(WorkspaceRepositoryOperationsTests.Definition(
            ("basic", "none", "none"),
            ["https://ghe.other.example"],
            ("Missing", "https://ghe.other.example/acme/Missing.git"),
            ("Other", "https://ghe.other.example/acme/Other.git")));

        var sync = new SyncRecorder(fixture);
        var outcome = await RestoreWorkspaceFlow.RunAsync(
            fixture.Operations, root.RepositoryId, "restored", sync.RequestAsync, progress: null, CancellationToken.None);

        Assert.True(outcome.Restore.Success, outcome.Restore.Error);
        Assert.True(outcome.SyncRequested);

        var panel = RestoreResultPanel.From(outcome);
        Assert.True(panel.HasContent);
        Assert.True(panel.HasUnresolved);
        Assert.Equal("Workspace restored", panel.Headline);
        Assert.Equal(["ghe.other.example"], panel.UnresolvedConnectors.ToArray());
        Assert.Equal(["acme/Missing", "acme/Other"], panel.UnresolvedRepositories.ToArray());
        Assert.Equal(
            ["2 repositories are not available on this computer.", "1 connector must be configured."],
            panel.SummaryLines.ToArray());
    }

    [Fact]
    public async Task A_failing_sync_keeps_the_restored_workspace_and_is_reported_as_a_sync_warning()
    {
        await using var fixture = await OperationsFixture.CreateAsync();
        var root = await fixture.SeedRootAsync();
        fixture.UseDefinition(WorkspaceRepositoryOperationsTests.Definition(("basic", "none", "none"), []));

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

        var panel = RestoreResultPanel.From(outcome);
        Assert.True(panel.HasContent);
        Assert.Equal("Workspace restored, but the initial Sync did not complete.", panel.Headline);
        Assert.Equal("Worker is busy.", panel.SyncError);
        Assert.False(panel.HasUnresolved);
    }

    [Fact]
    public void Result_panel_uses_singular_wording_for_one_item()
    {
        var outcome = new RestoreFlowOutcome(
            new RestoreWorkspaceResult { Success = true, WorkspaceId = 1, UnresolvedRepositories = ["acme/Api"] },
            true,
            null);

        Assert.Equal(["1 repository is not available on this computer."], RestoreResultPanel.From(outcome).SummaryLines.ToArray());
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
    public void Name_follows_the_selected_repository_until_the_user_types_their_own()
    {
        var (name, auto) = RestoreWorkspaceFlow.FollowRepositoryName(string.Empty, string.Empty, "Avr");
        Assert.Equal(("Avr", "Avr"), (name, auto));

        (name, auto) = RestoreWorkspaceFlow.FollowRepositoryName(name, auto, "Other");
        Assert.Equal(("Other", "Other"), (name, auto));

        // A typed name is never replaced by a later selection.
        (name, auto) = RestoreWorkspaceFlow.FollowRepositoryName("MyName", auto, "Third");
        Assert.Equal(("MyName", "Other"), (name, auto));

        // Clearing the field lets the selection supply the name again.
        (name, auto) = RestoreWorkspaceFlow.FollowRepositoryName(string.Empty, auto, "Third");
        Assert.Equal(("Third", "Third"), (name, auto));
    }

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
        var notChecked = RestoreWorkspaceFlow.EvaluateFolder(null);
        Assert.Equal(new FolderCheck(null, null), notChecked);

        var absent = RestoreWorkspaceFlow.EvaluateFolder(new WorkspaceDirectoryState(false, null, null));
        Assert.Equal(new FolderCheck(null, null), absent);

        var empty = RestoreWorkspaceFlow.EvaluateFolder(new WorkspaceDirectoryState(true, true, null));
        Assert.Null(empty.Error);
        Assert.Equal("The folder already exists and is empty. GrayMoon will use it.", empty.Note);

        var occupied = RestoreWorkspaceFlow.EvaluateFolder(new WorkspaceDirectoryState(true, false, null));
        Assert.Equal("This folder already contains files. Choose another Workspace name or move the existing files.", occupied.Error);
        Assert.Null(occupied.Note);
    }

    [Fact]
    public void Restore_is_enabled_only_for_a_valid_preflight_name_folder_and_worker()
    {
        var valid = new RestoreReadinessBuilder().Build();
        Assert.True(RestoreWorkspaceFlow.CanRestore(valid));

        // Unresolved Source repositories never block a valid definition.
        Assert.True(RestoreWorkspaceFlow.CanRestore(valid with
        {
            Preflight = Preview() with { MissingRepositories = ["acme/Missing"], MissingConnectors = ["ghe.example"] },
        }));

        Assert.False(RestoreWorkspaceFlow.CanRestore(valid with { Preflight = null }));
        Assert.False(RestoreWorkspaceFlow.CanRestore(valid with { Preflight = RestoreWorkspacePreflight.Failed("invalid") }));
        Assert.False(RestoreWorkspaceFlow.CanRestore(valid with { WorkerUnavailable = "Update the Worker." }));
        Assert.False(RestoreWorkspaceFlow.CanRestore(valid with { RepositoryId = null }));
        Assert.False(RestoreWorkspaceFlow.CanRestore(valid with { Name = "  " }));
        Assert.False(RestoreWorkspaceFlow.CanRestore(valid with { Name = "Taken" }));
        Assert.False(RestoreWorkspaceFlow.CanRestore(valid with { CheckingFolder = true }));
        Assert.False(RestoreWorkspaceFlow.CanRestore(valid with { Busy = true }));
        Assert.False(RestoreWorkspaceFlow.CanRestore(valid with
        {
            Folder = RestoreWorkspaceFlow.EvaluateFolder(new WorkspaceDirectoryState(true, false, null)),
        }));
        Assert.True(RestoreWorkspaceFlow.CanRestore(valid with
        {
            Folder = RestoreWorkspaceFlow.EvaluateFolder(new WorkspaceDirectoryState(true, true, null)),
        }));
    }

    [Fact]
    public void A_stale_preflight_answer_never_overwrites_a_newer_selection()
    {
        var gate = new RestorePreflightGate();

        var first = gate.Begin(1);
        Assert.True(gate.IsPending);
        var second = gate.Begin(2);

        // The answer for repository 1 arrives after the user selected repository 2.
        Assert.False(gate.TryComplete(first, selectedRepositoryId: 2));
        Assert.True(gate.IsPending);

        Assert.True(gate.TryComplete(second, selectedRepositoryId: 2));
        Assert.False(gate.IsPending);
    }

    [Fact]
    public void A_preflight_answer_is_dropped_after_the_selection_was_cleared()
    {
        var gate = new RestorePreflightGate();
        var token = gate.Begin(1);

        gate.Reset();

        Assert.False(gate.IsPending);
        Assert.False(gate.TryComplete(token, selectedRepositoryId: 1));
    }

    [Fact]
    public void Choices_read_org_slash_name_and_name_the_connector_only_when_ambiguous()
    {
        var choices = RestoreWorkspaceFlow.BuildChoices(
        [
            new RestoreRepositorySource(1, "ws", "acme", "github"),
            new RestoreRepositorySource(2, "ws", "acme", "ghe"),
            new RestoreRepositorySource(3, "Api", "acme", "github"),
            new RestoreRepositorySource(4, "solo", null, "github"),
        ]);

        Assert.Equal(
            ["acme/Api", "acme/ws (ghe)", "acme/ws (github)", "solo"],
            choices.Select(c => c.DisplayName).ToArray());
        Assert.Equal("ws", choices.Single(c => c.RepositoryId == 1).RepositoryName);

        Assert.Equal(
            choices.Select(c => (c.RepositoryId, c.DisplayName)),
            RestoreWorkspaceFlow.ToPickerChoices(choices).Select(c => (c.RepositoryId, c.DisplayName)));
    }

    [Fact]
    public void Selecting_a_repository_starts_a_preflight_and_a_failed_one_leaves_the_picker_usable()
    {
        var gate = new RestorePreflightGate();

        // Selecting repository 1 starts its check; Restore waits for it.
        var invalid = gate.Begin(1);
        Assert.True(gate.IsPending);
        Assert.True(gate.TryComplete(invalid, selectedRepositoryId: 1));

        // Its definition was invalid: Restore stays disabled, but choosing another repository checks that one.
        var failed = new RestoreReadinessBuilder().Build() with { Preflight = RestoreWorkspacePreflight.Failed("This repository does not contain .graymoon.json.") };
        Assert.False(RestoreWorkspaceFlow.CanRestore(failed));

        var next = gate.Begin(2);
        Assert.True(gate.IsPending);
        Assert.True(gate.TryComplete(next, selectedRepositoryId: 2));
        Assert.True(RestoreWorkspaceFlow.CanRestore(failed with { RepositoryId = 2, Preflight = Preview() }));
    }

    [Fact]
    public void A_typed_name_survives_later_repository_selections()
    {
        var (name, autoName) = RestoreWorkspaceFlow.FollowRepositoryName("", "", "GrayMoon.Workspace");
        Assert.Equal("GrayMoon.Workspace", name);

        // The user types their own name; neither another selection nor re-selecting the same repository replaces it.
        (name, autoName) = RestoreWorkspaceFlow.FollowRepositoryName("MyWorkspace", autoName, "Platform.Workspace");
        Assert.Equal("MyWorkspace", name);
        (name, _) = RestoreWorkspaceFlow.FollowRepositoryName(name, autoName, "GrayMoon.Workspace");
        Assert.Equal("MyWorkspace", name);
    }

    [Fact]
    public void Missing_summary_explains_what_will_be_missing()
    {
        Assert.Null(RestoreWorkspaceFlow.MissingSummary(Preview()));

        Assert.Equal(
            "2 repositories are not currently available in GrayMoon. 1 connector is not configured on this computer. "
            + "The Workspace can still be restored.",
            RestoreWorkspaceFlow.MissingSummary(Preview() with
            {
                MissingRepositories = ["acme/a", "acme/b"],
                MissingConnectors = ["ghe.example"],
            }));

        Assert.Equal(
            "1 connector is not configured on this computer. The Workspace can still be restored.",
            RestoreWorkspaceFlow.MissingSummary(Preview() with { MissingConnectors = ["ghe.example"] }));
    }

    [Fact]
    public void Restore_dialog_uses_the_large_modal_and_a_soft_validation_callout()
    {
        Assert.Equal("modal-dialog modal-lg modal-dialog-centered", RestoreWorkspaceModal.DialogCssClass);
        Assert.Contains("restore-validation-error", RestoreWorkspaceModal.ValidationCalloutClass, StringComparison.Ordinal);
        Assert.Contains("gm-callout--error", RestoreWorkspaceModal.ValidationCalloutClass, StringComparison.Ordinal);
    }

    [Fact]
    public void Folder_check_and_preflight_do_not_spin_the_restore_button()
    {
        Assert.False(RestoreWorkspaceFlow.ShowRestoreButtonSpinner(checkingFolder: true, preflightPending: false, restoreInProgress: false));
        Assert.False(RestoreWorkspaceFlow.ShowRestoreButtonSpinner(checkingFolder: false, preflightPending: true, restoreInProgress: false));
        Assert.False(RestoreWorkspaceFlow.ShowRestoreButtonSpinner(checkingFolder: true, preflightPending: true, restoreInProgress: true));
        Assert.True(RestoreWorkspaceFlow.ShowRestoreButtonSpinner(checkingFolder: false, preflightPending: false, restoreInProgress: true));

        Assert.False(RestoreWorkspaceFlow.CanRestore(new RestoreReadinessBuilder().Build() with { CheckingFolder = true }));
        Assert.Equal("Checking folder...", RestoreWorkspaceFlow.CheckingFolderStatus);
        Assert.Equal("Checking Workspace definition...", RestoreWorkspaceFlow.CheckingDefinitionStatus);
    }

    [Fact]
    public void Profile_labels_are_user_facing()
    {
        Assert.Equal(".NET Dependency", RestoreWorkspaceFlow.ProfileTypeLabel(WorkspaceType.DotNetDependency));
        Assert.Equal("Basic", RestoreWorkspaceFlow.ProfileTypeLabel(WorkspaceType.Basic));
        Assert.Equal("GitVersion", RestoreWorkspaceFlow.VersioningLabel(WorkspaceVersioningMode.GitVersion));
        Assert.Equal("No versioning", RestoreWorkspaceFlow.VersioningLabel(WorkspaceVersioningMode.None));
        Assert.Equal("GitHub Actions", RestoreWorkspaceFlow.CiLabel(WorkspaceCiProvider.GitHubActions));
        Assert.Equal("No CI", RestoreWorkspaceFlow.CiLabel(WorkspaceCiProvider.None));
    }

    private static RestoreWorkspacePreflight Preview() => new()
    {
        Success = true,
        DefinitionName = "ws",
        WorkspaceType = WorkspaceType.Basic,
        VersioningMode = WorkspaceVersioningMode.None,
        CiProvider = WorkspaceCiProvider.None,
        RepositoryCount = 3,
        ConnectorCount = 1,
    };

    private sealed class RestoreReadinessBuilder
    {
        public RestoreReadiness Build() => new(
            Busy: false,
            CheckingFolder: false,
            WorkerUnavailable: null,
            RepositoryId: 7,
            Preflight: Preview(),
            Name: "ws",
            ExistingNames: ["Taken"],
            Folder: new FolderCheck(null, null));
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
