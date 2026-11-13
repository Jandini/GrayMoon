using GrayMoon.App.Services.Jobs;
using GrayMoon.App.Services.Ui;
using Microsoft.Extensions.Logging.Abstractions;

namespace GrayMoon.App.Tests;

/// <summary>
/// Guards for the Workspace settings dialog: Enter on a tab or a footer button does not save the dialog, the
/// dialog scrolls instead of growing past the window, a save does not reload the page under a running job, and
/// the Workspaces list overlay follows the Workspace whose index is being built.
/// </summary>
public sealed class WorkspaceSettingsDialogGuardTests
{
    private static readonly string Modals = Path.Combine(Conventions.ConventionSource.AppDirectory, "Components", "Modals");

    [Fact]
    public void Enter_on_a_tab_the_lock_tab_or_a_footer_button_does_not_reach_the_dialog_save()
    {
        var razor = File.ReadAllText(Path.Combine(Modals, "WorkspaceModal.razor"));
        const string stop = "@onkeydown=\"HandleButtonAreaKeyDown\" @onkeydown:stopPropagation=\"true\"";

        Assert.Contains(stop, ElementStartingWith(razor, "<ul class=\"nav nav-tabs workspace-settings-tabs"));
        Assert.Contains(stop, ElementStartingWith(razor, "<div class=\"workspace-settings-panel @PanelClass(LockTab)\""));
        Assert.Contains(stop, ElementStartingWith(razor, "<div class=\"modal-footer\""));
    }

    [Fact]
    public void The_dialog_body_scrolls_so_the_footer_stays_in_the_window()
    {
        var razor = File.ReadAllText(Path.Combine(Modals, "WorkspaceModal.razor"));
        var css = File.ReadAllText(Path.Combine(Modals, "WorkspaceModal.razor.css"));

        Assert.Contains("modal-dialog-centered workspace-settings-dialog", razor);
        Assert.Contains(".workspace-settings-dialog > .modal-content {", css);
        Assert.Contains("max-height: calc(var(--modal-vh) - 2rem);", css);
        Assert.Contains(".workspace-settings-dialog .modal-body {", css);
        Assert.Contains("overflow-y: auto;", css);
    }

    [Fact]
    public void A_save_does_not_force_a_reload_while_a_job_runs_in_the_tab()
    {
        var host = File.ReadAllText(Path.Combine(Modals, "WorkspaceSettingsHost.razor"));
        var guard = host.IndexOf("if (JobService.HasRunningJobs)", StringComparison.Ordinal);
        var reload = host.IndexOf("forceLoad: true", StringComparison.Ordinal);

        Assert.True(guard >= 0, "The reload is not guarded by HasRunningJobs.");
        Assert.True(reload > guard, "The forced reload must come after the running-job check.");
        Assert.Equal(reload, host.LastIndexOf("forceLoad: true", StringComparison.Ordinal));
    }

    [Fact]
    public void The_list_overlay_follows_the_workspace_being_indexed_not_the_one_last_opened()
    {
        var host = File.ReadAllText(Path.Combine(Modals, "WorkspaceSettingsHost.razor"));

        Assert.Contains("CodeGraphActivation.ActiveWorkspaceId is not int workspaceId", host);
    }

    [Fact]
    public void Settings_opened_from_the_context_bar_write_the_files_of_the_selected_context()
    {
        var bar = File.ReadAllText(Path.Combine(Conventions.ConventionSource.AppDirectory, "Components", "Features", "WorkspaceContextBar.razor"));
        var host = File.ReadAllText(Path.Combine(Modals, "WorkspaceSettingsHost.razor"));

        Assert.Contains("OpenAsync(WorkspaceId, SelectedContextId)", bar);
        Assert.Contains("ManifestFor(_contextId).SyncDefinitionToDiskAsync(workspaceId)", host);
        Assert.Matches(@"CurrentPath\(\),\s+_contextId\);", host);
        // No save path of the dialog goes around the context.
        Assert.DoesNotContain("ManifestService.SyncDefinitionToDiskAsync", host);
        Assert.DoesNotContain("ManifestService.GetCodeGraphSwitchAsync", host);
    }

    [Fact]
    public async Task HasRunningJobs_is_true_for_a_job_started_on_any_page_and_false_when_it_ends()
    {
        var runner = new WorkspaceOperationRunner(NullLogger<WorkspaceOperationRunner>.Instance);
        using var service = new BackgroundJobService(runner, new ToastService(), NullLogger<BackgroundJobService>.Instance);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Assert.False(service.HasRunningJobs);

        service.StartJob("/workspaces/4/files", "Updating...", async (_, _) =>
        {
            entered.SetResult();
            await release.Task;
        });
        await entered.Task;

        Assert.True(service.HasRunningJobs);

        var operation = runner.GetRunning(4);
        Assert.NotNull(operation);
        release.SetResult();
        await operation.WhenCompleted;

        Assert.False(service.HasRunningJobs);
    }

    /// <summary>The opening tag that starts with <paramref name="start"/>, up to its closing <c>&gt;</c>.</summary>
    private static string ElementStartingWith(string razor, string start)
    {
        var from = razor.IndexOf(start, StringComparison.Ordinal);
        Assert.True(from >= 0, $"Element not found: {start}");
        var to = razor.IndexOf('>', from);
        return razor[from..(to + 1)];
    }
}
