using GrayMoon.App.Components.Pages;

namespace GrayMoon.App.Tests;

public sealed class WorkspaceActionsSuccessFilterTests
{
    [Fact]
    public void A_running_workflow_hides_success()
    {
        var (show, mode) = Apply(show: true, WorkspaceActions.SuccessFilterMode.Automatic, anyRunning: true, deferRestore: false);

        Assert.False(show);
        Assert.Equal(WorkspaceActions.SuccessFilterMode.Automatic, mode);
    }

    [Fact]
    public void Success_comes_back_when_nothing_is_running()
    {
        var (show, mode) = Apply(show: true, WorkspaceActions.SuccessFilterMode.Automatic, anyRunning: true, deferRestore: false);

        (show, mode) = Apply(show, mode, anyRunning: false, deferRestore: false);

        Assert.True(show);
        Assert.Equal(WorkspaceActions.SuccessFilterMode.Automatic, mode);
    }

    [Fact]
    public void The_next_run_hides_success_again()
    {
        var (show, mode) = Apply(show: true, WorkspaceActions.SuccessFilterMode.Automatic, anyRunning: true, deferRestore: false);
        (show, mode) = Apply(show, mode, anyRunning: false, deferRestore: false);

        (show, mode) = Apply(show, mode, anyRunning: true, deferRestore: false);

        Assert.False(show);
        Assert.Equal(WorkspaceActions.SuccessFilterMode.Automatic, mode);
    }

    [Fact]
    public void Clicking_success_while_a_run_is_going_keeps_those_rows_until_the_run_finishes()
    {
        var (show, mode) = Apply(show: true, WorkspaceActions.SuccessFilterMode.Automatic, anyRunning: true, deferRestore: false);

        (show, mode) = WorkspaceActions.ToggleSuccessFilter(show, anyRunning: true);
        Assert.True(show);
        Assert.Equal(WorkspaceActions.SuccessFilterMode.ShownByUser, mode);

        (show, mode) = Apply(show, mode, anyRunning: true, deferRestore: false);
        Assert.True(show);
        Assert.Equal(WorkspaceActions.SuccessFilterMode.ShownByUser, mode);

        (show, mode) = Apply(show, mode, anyRunning: false, deferRestore: false);
        Assert.True(show);
        Assert.Equal(WorkspaceActions.SuccessFilterMode.Automatic, mode);
    }

    [Fact]
    public void Hiding_success_again_while_a_run_is_going_brings_it_back_when_the_run_finishes()
    {
        var (show, mode) = Apply(show: true, WorkspaceActions.SuccessFilterMode.Automatic, anyRunning: true, deferRestore: false);
        (show, mode) = WorkspaceActions.ToggleSuccessFilter(show, anyRunning: true);

        (show, mode) = WorkspaceActions.ToggleSuccessFilter(show, anyRunning: true);

        Assert.False(show);
        Assert.Equal(WorkspaceActions.SuccessFilterMode.Automatic, mode);

        (show, mode) = Apply(show, mode, anyRunning: false, deferRestore: false);
        Assert.True(show);
        Assert.Equal(WorkspaceActions.SuccessFilterMode.Automatic, mode);
    }

    [Fact]
    public void A_refresh_that_finds_a_run_hides_success_and_does_not_show_it_again_until_the_refresh_finishes()
    {
        var (show, mode) = Apply(show: true, WorkspaceActions.SuccessFilterMode.Automatic, anyRunning: false, deferRestore: true);
        Assert.True(show);

        (show, mode) = Apply(show, mode, anyRunning: true, deferRestore: true);
        Assert.False(show);

        (show, mode) = Apply(show, mode, anyRunning: false, deferRestore: true);
        Assert.False(show);
        Assert.Equal(WorkspaceActions.SuccessFilterMode.Automatic, mode);

        (show, mode) = Apply(show, mode, anyRunning: false, deferRestore: false);
        Assert.True(show);
    }

    [Fact]
    public void Turning_success_off_while_nothing_is_running_stays_off()
    {
        var (show, mode) = WorkspaceActions.ToggleSuccessFilter(showSuccess: true, anyRunning: false);
        Assert.False(show);
        Assert.Equal(WorkspaceActions.SuccessFilterMode.HiddenByUser, mode);

        (show, mode) = Apply(show, mode, anyRunning: true, deferRestore: false);
        Assert.False(show);
        Assert.Equal(WorkspaceActions.SuccessFilterMode.HiddenByUser, mode);

        (show, mode) = Apply(show, mode, anyRunning: false, deferRestore: false);
        Assert.False(show);
        Assert.Equal(WorkspaceActions.SuccessFilterMode.HiddenByUser, mode);

        (show, mode) = WorkspaceActions.ToggleSuccessFilter(show, anyRunning: false);
        Assert.True(show);
        Assert.Equal(WorkspaceActions.SuccessFilterMode.Automatic, mode);
    }

    private static (bool Show, WorkspaceActions.SuccessFilterMode Mode) Apply(
        bool show, WorkspaceActions.SuccessFilterMode mode, bool anyRunning, bool deferRestore) =>
        WorkspaceActions.ApplyRunningSuccessFilter(show, mode, anyRunning, deferRestore);
}
