using System.Runtime.CompilerServices;
using GrayMoon.App.Components.Shared;
using GrayMoon.App.Models;
using GrayMoon.App.Services.Queries;
using GrayMoon.App.Services.Ui;

namespace GrayMoon.App.Tests;

/// <summary>
/// Regression tests for the modal and shared-UI findings of the 2026-11-10 review: a reopened logs modal does
/// not show another run's logs (28), the notification popup acts on every repository of the selected context
/// (29), and the P3 items on the live terminal, the loading overlay, the connector, add-files and switch-branch
/// dialogs and the Workspaces delete dialog.
/// </summary>
public sealed class ModalAndSharedUiGuardTests
{
    // --- LoadGeneration (28, AddFilesModal) ---

    [Fact]
    public void A_newer_load_cancels_the_older_one_and_drops_its_result()
    {
        using var load = new LoadGeneration();

        var (first, firstToken) = load.Begin();
        Assert.True(load.IsCurrent(first));

        var (second, secondToken) = load.Begin();

        Assert.False(load.IsCurrent(first));
        Assert.True(firstToken.IsCancellationRequested);
        Assert.True(load.IsCurrent(second));
        Assert.False(secondToken.IsCancellationRequested);
    }

    [Fact]
    public void Closing_ends_the_running_load_without_starting_another()
    {
        using var load = new LoadGeneration();
        var (generation, token) = load.Begin();

        load.Invalidate();

        Assert.False(load.IsCurrent(generation));
        Assert.True(token.IsCancellationRequested);
    }

    [Fact]
    public void A_reopen_before_the_next_load_starts_already_drops_the_old_result()
    {
        // The modal resets in OnParametersSet and starts the load a render later: the old load must be stale in between.
        using var load = new LoadGeneration();
        var (old, _) = load.Begin();

        load.Invalidate();
        Assert.False(load.IsCurrent(old));

        var (next, _) = load.Begin();
        Assert.False(load.IsCurrent(old));
        Assert.True(load.IsCurrent(next));
    }

    [Fact]
    public void Dispose_cancels_and_can_be_repeated()
    {
        var load = new LoadGeneration();
        var (generation, token) = load.Begin();

        load.Dispose();
        load.Dispose();

        Assert.True(token.IsCancellationRequested);
        Assert.False(load.IsCurrent(generation));
    }

    [Fact]
    public void Logs_modal_guards_every_result_with_the_load_generation()
    {
        var modal = ReadComponent("Modals", "GhaLogsModal.razor");
        var load = MethodBody(modal, "private async Task LoadLogsAsync()");

        Assert.Contains("_load.Begin()", load);
        Assert.Contains("GetWorkflowRunJobsAsync(connector, owner, repositoryName, RunId, ct)", load);
        Assert.Contains("GetJobLogsAsync(connector, owner, repositoryName, job.Id, ct)", load);
        // The shared list is never appended to by a load; it writes to the list it captured.
        Assert.DoesNotContain("_jobs.Add(", load);
        Assert.DoesNotContain("_jobs.Clear()", modal);
        // After every await that can outlive the open: connector, job list, each job log.
        Assert.True(CountOf(load, "if (!_load.IsCurrent(generation)) return;") >= 3);
        Assert.Contains("public void Dispose() => _load.Dispose();", modal);
    }

    [Fact]
    public void Add_files_search_is_superseded_instead_of_dropped()
    {
        var modal = ReadComponent("Modals", "AddFilesModal.razor");
        var search = MethodBody(modal, "private async Task SearchAsync()");

        // The old early return silently ignored a new pattern or repository while a search was running.
        Assert.DoesNotContain("|| isSearching) return;", search);
        Assert.Contains("_search.Begin()", search);
        Assert.Contains("selectedRepositoryName, ct)", search);
        Assert.Contains("if (!_search.IsCurrent(generation)) return;", search);
    }

    // --- Notification popup (29) ---

    [Fact]
    public void Default_branch_warning_covers_repositories_past_the_listed_rows()
    {
        var links = Enumerable.Range(1, WorkspacePendingActionsService.MaxListedActionRepos + 5)
            .Select(i => Link(i, branch: "feature/x", unmatchedDeps: 1, outgoing: 1))
            .ToList();
        // The only repository on its default branch is past the popup's list cap.
        var lastId = links.Count;
        links[^1] = Link(lastId, branch: "main", unmatchedDeps: 1, outgoing: 2);

        var update = NotificationPanelRepoSets.UpdateOnDefaultBranch(links);
        var push = NotificationPanelRepoSets.PushOnDefaultBranch(links);

        Assert.Equal($"repo-{lastId}", Assert.Single(update).RepositoryName);
        Assert.Equal("main", Assert.Single(push).DefaultBranchName);
        Assert.Equal(links.Count, NotificationPanelRepoSets.WithOutgoing(links).Count);
    }

    [Fact]
    public void Repository_sets_follow_the_selected_context_rows()
    {
        // Rows as the Feature context reports them: repo 1 is on a tag there, repo 2 has the Feature's own counters.
        var featureRows = new[]
        {
            Link(1, branch: null, tag: "v1.0.0", unmatchedDeps: 2, incoming: 3, outgoing: 1, outOfDateFiles: 1),
            Link(2, branch: "feature/x", unmatchedDeps: 0, incoming: 4, outgoing: 0, outOfDateFiles: 1),
            Link(3, branch: "feature/x", unmatchedDeps: 1, incoming: 0, outgoing: 2),
            Link(4, branch: "feature/x"),
        };

        Assert.Equal([2, 3], NotificationPanelRepoSets.NeedingUpdate(featureRows).Order());
        Assert.Equal([2], NotificationPanelRepoSets.WithIncoming(featureRows));
        Assert.Equal([("repo-3", 2)], NotificationPanelRepoSets.WithOutgoing(featureRows));
        Assert.Empty(NotificationPanelRepoSets.UpdateOnDefaultBranch(featureRows));
        Assert.Empty(NotificationPanelRepoSets.PushOnDefaultBranch(featureRows));
    }

    [Fact]
    public void A_repository_without_a_known_default_branch_is_not_warned_about()
    {
        var links = new[] { Link(1, branch: "main", defaultBranch: null, unmatchedDeps: 1, outgoing: 1) };

        Assert.Empty(NotificationPanelRepoSets.UpdateOnDefaultBranch(links));
        Assert.Empty(NotificationPanelRepoSets.PushOnDefaultBranch(links));
    }

    [Fact]
    public void Notification_popup_reads_the_selected_context_and_not_the_capped_rows_or_the_workspace_link()
    {
        var panel = ReadComponent("Shared", "WorkspaceActionNotificationPanel.razor");

        foreach (var signature in new[]
        {
            "private async Task RunUpdateAsync(WorkspaceNotification n)",
            "private async Task RunPushAsync(WorkspaceNotification n)",
            "private async Task RunUndoPushAsync(WorkspaceNotification n)",
        })
        {
            var body = MethodBody(panel, signature);
            Assert.DoesNotContain("n.Repos", body);
            Assert.Contains("TryLoadSelectedContextLinksAsync(", body);
        }

        var update = MethodBody(panel, "private async Task StartUpdateJobAsync(int workspaceId, WorkspaceFeatureContextId contextId)");
        var pull = MethodBody(panel, "private async Task RunPullAsync(int workspaceId)");
        foreach (var body in new[] { update, pull })
        {
            Assert.DoesNotContain("wr.IsOnTag", body);
            Assert.Contains("LoadContextLinksAsync(scope.ServiceProvider, workspaceId, contextId, ct)", body);
        }

        // The context of these jobs is fixed before the job is queued.
        foreach (var body in new[]
        {
            update,
            JobLambda(pull),
            MethodBody(panel, "private Task StartPushJobAsync(int workspaceId, WorkspaceFeatureContextId contextId)"),
            MethodBody(panel, "private Task StartUndoPushJobAsync(int workspaceId, WorkspaceFeatureContextId contextId, bool keepChanges)"),
            MethodBody(panel, "private async Task ExecutePushAsync("),
        })
        {
            Assert.DoesNotContain("ResolveOperationContextAsync(", body);
        }
    }

    [Fact]
    public void Notification_popup_starts_its_hub_through_the_shared_helper()
    {
        var panel = ReadComponent("Shared", "WorkspaceActionNotificationPanel.razor");

        Assert.Contains("await WorkspaceSyncHubConnectionHelper.TryStartAsync(_hubConnection, Logger);", panel);
        Assert.DoesNotContain("_hubConnection.StartAsync()", panel);
    }

    // --- GhaWorkflowLiveTerminal ---

    [Fact]
    public void Live_terminal_restarts_only_when_the_run_changes()
    {
        var run1 = new LiveTerminalTarget("gh", "owner", "repo", 1);
        var run2 = run1 with { RunId = 2 };
        var noRun = run1 with { RunId = 0 };

        Assert.True(LiveTerminalPolling.ShouldRestart(null, run1));
        Assert.False(LiveTerminalPolling.ShouldRestart(null, noRun));
        Assert.False(LiveTerminalPolling.ShouldRestart(run1, run1));
        Assert.True(LiveTerminalPolling.ShouldRestart(run1, run2));
        Assert.True(LiveTerminalPolling.ShouldRestart(run1, run1 with { RepositoryName = "other" }));
        // The run went away: the loops of the old run stop.
        Assert.True(LiveTerminalPolling.ShouldRestart(run1, noRun));
    }

    [Fact]
    public void Live_terminal_keeps_polling_after_a_failed_poll_and_never_throws_from_dispose()
    {
        var terminal = ReadComponent("Shared", "GhaWorkflowLiveTerminal.razor");
        var loop = MethodBody(terminal, "private async Task PollLoopAsync(GhaWorkflowLiveFeedState feedState, CancellationToken cancellationToken)");
        var dispose = MethodBody(terminal, "public async ValueTask DisposeAsync()");

        Assert.Contains("catch (Exception ex)", loop);
        Assert.Contains("LiveTerminalPolling.RetryDelayAfterFailureMs", loop);
        Assert.Contains("catch (Exception ex)", dispose);
        Assert.Contains("LiveTerminalPolling.ShouldRestart(_polledTarget, target)", terminal);
    }

    // --- LoadingOverlay ---

    [Fact]
    public void Matrix_overlay_makes_no_interop_call_when_nothing_changed()
    {
        var first = MatrixOverlayTransition.Decide(false, false, null, isVisible: true, useMatrix: true, dimPercent: 45);
        Assert.Equal(new MatrixOverlayInterop(SetDim: true, Start: true, Stop: false), first);

        // Every later render of the same visible overlay (a terminal line, a progress message).
        var same = MatrixOverlayTransition.Decide(true, true, 45, isVisible: true, useMatrix: true, dimPercent: 45);
        Assert.False(same.Any);

        var dimChanged = MatrixOverlayTransition.Decide(true, true, 45, isVisible: true, useMatrix: true, dimPercent: 60);
        Assert.Equal(new MatrixOverlayInterop(SetDim: true, Start: false, Stop: false), dimChanged);

        var hidden = MatrixOverlayTransition.Decide(true, true, 45, isVisible: false, useMatrix: true, dimPercent: 45);
        Assert.Equal(new MatrixOverlayInterop(SetDim: false, Start: false, Stop: true), hidden);

        var matrixOff = MatrixOverlayTransition.Decide(true, true, 45, isVisible: true, useMatrix: false, dimPercent: 45);
        Assert.Equal(new MatrixOverlayInterop(SetDim: false, Start: false, Stop: true), matrixOff);

        // Matrix off, or never shown: nothing to tell the canvas.
        Assert.False(MatrixOverlayTransition.Decide(false, false, null, isVisible: true, useMatrix: false, dimPercent: 45).Any);
        Assert.False(MatrixOverlayTransition.Decide(false, true, 45, isVisible: false, useMatrix: true, dimPercent: 45).Any);
    }

    [Fact]
    public void Loading_overlay_tolerates_a_closed_circuit()
    {
        var overlay = ReadComponent("Shared", "LoadingOverlay.razor");

        Assert.Contains("MatrixOverlayTransition.Decide(", overlay);
        Assert.Contains("ex is JSDisconnectedException or InvalidOperationException or OperationCanceledException", overlay);
    }

    // --- ConnectorModal ---

    [Theory]
    [InlineData(true, false, false, true)]
    [InlineData(true, false, true, false)]  // Delete is already running
    [InlineData(true, true, false, false)]  // key held down
    [InlineData(false, false, false, false)]
    public void Enter_submits_only_when_the_button_would(bool isPlainEnter, bool isRepeat, bool isSubmitting, bool expected)
    {
        Assert.Equal(expected, ModalSubmitGuard.CanSubmit(isPlainEnter, isRepeat, isSubmitting));
    }

    [Fact]
    public void Connector_delete_by_enter_goes_through_the_submit_guard()
    {
        var modal = ReadComponent("Modals", "ConnectorModal.razor");

        Assert.Contains("ModalSubmitGuard.CanSubmit(ModalKeyboard.IsPlainEnter(e), e.Repeat, IsSubmitting)", modal);
        Assert.DoesNotContain("ModalKeyboard.IsPlainEnter(e) && Mode == ConnectorModalMode.Delete", modal);
    }

    // --- SwitchBranchModal ---

    [Theory]
    [InlineData("origin/feature/x", "feature/x")]
    [InlineData("Origin/main", "main")]
    [InlineData("feature/x", "feature/x")]
    [InlineData("feature/origin/x", "feature/origin/x")]
    public void Occupancy_is_looked_up_by_the_local_branch_name(string selected, string expected)
    {
        Assert.Equal(expected, BranchOccupancyKey.For(selected));
    }

    [Fact]
    public void Switch_branch_checks_occupancy_for_a_branch_selected_on_the_remotes_tab()
    {
        var modal = ReadComponent("Modals", "SwitchBranchModal.razor");
        var disabled = MethodBody(modal, "private bool IsCheckoutDisabled()");

        Assert.Contains("activeTab == \"remote\" ? BranchOccupancyKey.For(selectedBranch) : selectedBranch", disabled);
        Assert.Contains("TryGetOccupancy(occupancyKey, out var occ)", disabled);
    }

    // --- Workspaces delete dialog ---

    [Fact]
    public void Workspaces_delete_dialog_takes_focus_once_per_open()
    {
        var page = ReadComponent("Pages", "Workspaces.razor");

        Assert.Contains("if (showDeleteModal && !_deleteModalFocused)", page);
        Assert.DoesNotContain("if (showDeleteModal)\r\n            await _deleteModalElement.FocusAsync();", page);
        Assert.DoesNotContain("if (showDeleteModal)\n            await _deleteModalElement.FocusAsync();", page);
    }

    // --- helpers ---

    private static WorkspaceRepositoryLinkListItemDto Link(
        int id,
        string? branch,
        string? tag = null,
        string? defaultBranch = "main",
        int unmatchedDeps = 0,
        int incoming = 0,
        int outgoing = 0,
        int outOfDateFiles = 0) =>
        new(
            WorkspaceRepositoryId: id,
            WorkspaceId: 1,
            RepositoryId: id,
            RepositoryName: $"repo-{id}",
            CloneUrl: $"https://example.test/repo-{id}.git",
            GitVersion: null,
            BranchName: branch,
            CheckedOutTag: tag,
            DefaultBranchName: defaultBranch,
            OutgoingCommits: outgoing,
            IncomingCommits: incoming,
            DefaultBranchBehindCommits: null,
            DefaultBranchAheadCommits: null,
            BranchHasUpstream: true,
            SyncStatus: RepoSyncStatus.InSync,
            DependencyLevel: 1,
            Dependencies: 0,
            UnmatchedDeps: unmatchedDeps,
            OutOfDateFileRepos: outOfDateFiles,
            RepositoryType: null,
            HasNewerTag: null,
            HasSelfFileVersionToken: null,
            PullRequestState: null,
            PullRequestNumber: null,
            PullRequestHtmlUrl: null,
            PullRequestMergedAt: null,
            PullRequestMergeable: null,
            PullRequestMergeableState: null,
            PullRequestChangedFiles: null,
            Archived: false,
            UncommittedChangedFileCount: 0);

    private static int CountOf(string text, string value)
    {
        var count = 0;
        for (var i = text.IndexOf(value, StringComparison.Ordinal); i >= 0; i = text.IndexOf(value, i + value.Length, StringComparison.Ordinal))
            count++;
        return count;
    }

    /// <summary>The body of the job lambda passed to <c>JobService.StartJob</c> in a method.</summary>
    private static string JobLambda(string method)
    {
        var start = method.IndexOf("async (job, ct) =>", StringComparison.Ordinal);
        Assert.True(start >= 0, "The method starts no job.");
        return Block(method, start);
    }

    private static string MethodBody(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Method '{signature}' was not found.");
        return Block(source, start);
    }

    private static string Block(string source, int from)
    {
        var open = source.IndexOf('{', from);
        var depth = 0;
        for (var i = open; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0)
                return source[open..(i + 1)];
        }
        throw new InvalidOperationException("The block has no closing brace.");
    }

    private static string ReadComponent(string folder, string fileName) =>
        File.ReadAllText(Path.Combine(ComponentsDirectory(), folder, fileName));

    private static string ComponentsDirectory([CallerFilePath] string thisFile = "")
    {
        // The build output can live outside the repository (--artifacts-path), so fall back to this source file's location.
        foreach (var origin in new[] { AppContext.BaseDirectory, Path.GetDirectoryName(thisFile)! })
        {
            var dir = new DirectoryInfo(origin);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "GrayMoon.slnx")))
                dir = dir.Parent;
            if (dir != null)
                return Path.Combine(dir.FullName, "src", "GrayMoon.App", "Components");
        }
        throw new InvalidOperationException("GrayMoon.slnx was not found above the test output or the test source.");
    }
}
