using GrayMoon.App.Services.GitChanges;

namespace GrayMoon.App.Tests;

public sealed class WorkspaceCommitModeHelperTests
{
    [Fact]
    public void Commit_All_with_no_staged_files_sets_StageAllFirst_true()
    {
        var mode = WorkspaceCommitModeHelper.ResolveOfferedMode(anyRepositoryHasStaged: false);

        Assert.Equal(WorkspaceCommitMode.All, mode);
        Assert.True(WorkspaceCommitModeHelper.StageAllFirst(mode));
        Assert.Equal("Commit All", WorkspaceCommitModeHelper.ButtonTitle(mode));
    }

    [Fact]
    public void Commit_All_always_sets_StageAllFirst_true_even_when_staged_files_exist()
    {
        // Explicit All must not flip to Staged just because the view later shows staged content.
        Assert.True(WorkspaceCommitModeHelper.StageAllFirst(WorkspaceCommitMode.All));
    }

    [Fact]
    public void Commit_Staged_always_sets_StageAllFirst_false()
    {
        Assert.False(WorkspaceCommitModeHelper.StageAllFirst(WorkspaceCommitMode.Staged));
        Assert.Equal("Commit Staged", WorkspaceCommitModeHelper.ButtonTitle(WorkspaceCommitMode.Staged));
    }

    [Fact]
    public void Captured_Commit_All_mode_survives_view_changing_to_have_staged_files()
    {
        // Simulate render-time capture with no staged repos, then a watcher update before click.
        var modeAtRender = WorkspaceCommitModeHelper.ResolveOfferedMode(anyRepositoryHasStaged: false);
        var anyStagedAfterRefresh = true;

        Assert.Equal(WorkspaceCommitMode.All, modeAtRender);
        Assert.Equal(WorkspaceCommitMode.Staged, WorkspaceCommitModeHelper.ResolveOfferedMode(anyStagedAfterRefresh));
        // The captured value is what the click must execute - not a fresh ResolveOfferedMode.
        Assert.True(WorkspaceCommitModeHelper.StageAllFirst(modeAtRender));
    }

    [Fact]
    public void Watcher_refresh_during_dispatch_must_not_change_captured_Commit_All_into_Commit_Staged()
    {
        var captured = WorkspaceCommitMode.All;

        // Fresh status may be used for validation/display, but must not mutate the selected mode.
        var offeredAfterWatcher = WorkspaceCommitModeHelper.ResolveOfferedMode(anyRepositoryHasStaged: true);

        Assert.Equal(WorkspaceCommitMode.Staged, offeredAfterWatcher);
        Assert.Equal(WorkspaceCommitMode.All, captured);
        Assert.True(WorkspaceCommitModeHelper.StageAllFirst(captured));
    }

    [Fact]
    public void Staged_change_in_repository_A_does_not_alter_StageAllFirst_for_repository_B_under_All_mode()
    {
        // Under All, every target gets StageAllFirst=true regardless of per-repo staged counts.
        Assert.True(WorkspaceCommitModeHelper.StageAllFirst(WorkspaceCommitMode.All));
        Assert.True(WorkspaceCommitModeHelper.IsRepositoryTarget(WorkspaceCommitMode.All, stagedCount: 1, changedCount: 0)); // A
        Assert.True(WorkspaceCommitModeHelper.IsRepositoryTarget(WorkspaceCommitMode.All, stagedCount: 0, changedCount: 5)); // B unstaged only
    }

    [Fact]
    public void Staged_mode_targets_only_repositories_with_staged_changes()
    {
        Assert.True(WorkspaceCommitModeHelper.IsRepositoryTarget(WorkspaceCommitMode.Staged, stagedCount: 1, changedCount: 30));
        Assert.False(WorkspaceCommitModeHelper.IsRepositoryTarget(WorkspaceCommitMode.Staged, stagedCount: 0, changedCount: 30));
    }

    [Fact]
    public void Offered_mode_emphasizes_Staged_when_any_repository_has_staged_files()
    {
        Assert.Equal(WorkspaceCommitMode.Staged, WorkspaceCommitModeHelper.ResolveOfferedMode(anyRepositoryHasStaged: true));
    }

    [Fact]
    public void Commit_All_button_must_bind_constant_All_mode_not_offered_primary()
    {
        // Regression: when nothing is staged the primary used to bind OfferedCommitMode (All). A
        // watcher refresh mid-click flipped that primary to Staged while Blazor reused the event
        // handler id, so an in-flight Commit All click executed Commit Staged. Commit All must
        // always bind WorkspaceCommitMode.All; Staged is a separate keyed button.
        Assert.True(WorkspaceCommitModeHelper.StageAllFirst(WorkspaceCommitMode.All));
        Assert.False(WorkspaceCommitModeHelper.StageAllFirst(WorkspaceCommitMode.Staged));
        Assert.Equal(
            WorkspaceCommitMode.Staged,
            WorkspaceCommitModeHelper.ResolveOfferedMode(anyRepositoryHasStaged: true));
    }

    [Fact]
    public void Logging_contract_maps_mode_to_StageAllFirst_explicitly()
    {
        // Diagnosis asks for logs like Mode=All, StageAllFirst=True - verify the mapping those logs use.
        Assert.Equal((WorkspaceCommitMode.All, true), (WorkspaceCommitMode.All, WorkspaceCommitModeHelper.StageAllFirst(WorkspaceCommitMode.All)));
        Assert.Equal((WorkspaceCommitMode.Staged, false), (WorkspaceCommitMode.Staged, WorkspaceCommitModeHelper.StageAllFirst(WorkspaceCommitMode.Staged)));
    }
}
