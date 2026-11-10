using GrayMoon.App.Services.Ui;

namespace GrayMoon.App.Tests;

/// <summary>
/// The warning toast: work that finished but needs attention (a push whose package restore failed) must not
/// look like a plain success or like a failure.
/// </summary>
public sealed class ToastWarningStyleTests
{
    [Fact]
    public void A_warning_toast_is_neither_a_plain_message_nor_an_error()
    {
        var toast = new ToastService();
        var raised = 0;
        toast.OnShow += () => raised++;

        toast.ShowWarning("Push finished, but package restore failed in 1 repository.");

        Assert.True(toast.IsVisible);
        Assert.True(toast.IsWarning);
        Assert.False(toast.IsError);
        Assert.Equal("Push finished, but package restore failed in 1 repository.", toast.Message);
        Assert.Equal(1, toast.ShownId);
        Assert.Equal(1, raised);
    }

    [Fact]
    public void The_style_always_follows_the_latest_toast()
    {
        var toast = new ToastService();

        toast.ShowWarning("warning");
        toast.Show("plain");
        Assert.False(toast.IsWarning);
        Assert.False(toast.IsError);

        toast.ShowWarning("warning");
        toast.ShowError("error");
        Assert.False(toast.IsWarning);
        Assert.True(toast.IsError);

        toast.ShowWarning("warning");
        Assert.True(toast.IsWarning);
        Assert.False(toast.IsError);

        toast.Hide();
        Assert.False(toast.IsWarning);
        Assert.False(toast.IsVisible);
    }

    [Fact]
    public void The_component_gives_a_warning_its_own_style_and_the_long_display_time()
    {
        var app = Path.Combine(Conventions.ConventionSource.AppDirectory, "Components", "Shared");
        var razor = File.ReadAllText(Path.Combine(app, "Toast.razor"));
        var css = File.ReadAllText(Path.Combine(app, "Toast.razor.css"));

        Assert.Contains("isWarning ? \"toast-bubble--warning\"", razor);
        Assert.Contains("ToastService.IsError || ToastService.IsWarning ? 6_000 : 2_500", razor);
        Assert.Contains(".toast-bubble--warning {", css);
    }

    [Fact]
    public void A_push_that_finished_with_a_restore_warning_uses_the_warning_toast_on_every_surface()
    {
        var components = Path.Combine(Conventions.ConventionSource.AppDirectory, "Components");
        foreach (var file in new[]
                 {
                     Path.Combine("Pages", "WorkspaceRepositories.Push.cs"),
                     Path.Combine("Pages", "WorkspaceRepositories.Update.cs"),
                     Path.Combine("Pages", "WorkspaceGitChanges.MultiRepo.cs"),
                     Path.Combine("Shared", "WorkspaceActionNotificationPanel.razor"),
                 })
        {
            Assert.Contains("ToastService.ShowWarning(", File.ReadAllText(Path.Combine(components, file)));
        }
    }
}