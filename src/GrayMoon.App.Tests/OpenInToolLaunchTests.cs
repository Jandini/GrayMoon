using GrayMoon.App.Components.Features;
using GrayMoon.App.Services.Ui;

namespace GrayMoon.App.Tests;

public sealed class OpenInToolLaunchTests
{
    [Fact]
    public void Button_disables_as_soon_as_the_click_is_accepted()
    {
        var launch = OpenInToolLaunch.Begin(FeatureOpenInTools.Explorer);

        Assert.Equal(FeatureOpenInTools.Explorer, launch.ToolId);
        Assert.True(launch.IsButtonDisabled);
        Assert.False(launch.AttemptFinished);
        Assert.Equal(0, launch.FeedbackId);
        Assert.False(launch.IsFeedbackShowing(1, toastVisible: true));
    }

    [Fact]
    public void Finishing_the_post_leaves_the_button_disabled_while_the_toast_is_showing()
    {
        var launch = OpenInToolLaunch.Begin(FeatureOpenInTools.Cursor);
        launch.NoteFeedback(1);

        launch.FinishAttempt();

        Assert.True(launch.AttemptFinished);
        Assert.True(launch.IsButtonDisabled);
        Assert.True(launch.IsFeedbackShowing(1, toastVisible: true));
    }

    [Fact]
    public void Button_enables_when_the_toast_ends_after_the_post()
    {
        var launch = OpenInToolLaunch.Begin(FeatureOpenInTools.Terminal);
        launch.NoteFeedback(4);
        launch.FinishAttempt();

        launch.EndFeedback();

        Assert.False(launch.IsButtonDisabled);
        Assert.False(launch.IsFeedbackShowing(4, toastVisible: false));
    }

    [Fact]
    public void Ending_the_toast_does_not_post_the_tool()
    {
        var launch = OpenInToolLaunch.Begin(FeatureOpenInTools.VsCode);
        launch.NoteFeedback(2);

        launch.EndFeedback();

        Assert.False(launch.AttemptFinished);
        Assert.True(launch.IsButtonDisabled);

        launch.FinishAttempt();

        Assert.False(launch.IsButtonDisabled);
    }

    [Fact]
    public void A_different_toast_is_not_this_click()
    {
        var launch = OpenInToolLaunch.Begin(FeatureOpenInTools.Explorer);
        launch.NoteFeedback(3);

        Assert.True(launch.IsFeedbackShowing(3, toastVisible: true));
        Assert.False(launch.IsFeedbackShowing(3, toastVisible: false));
        Assert.False(launch.IsFeedbackShowing(9, toastVisible: true));
    }

    [Fact]
    public void Every_tool_disables_on_click_and_enables_when_its_toast_ends()
    {
        long id = 1;
        foreach (var tool in FeatureOpenInTools.All)
        {
            var launch = OpenInToolLaunch.Begin(tool.Id);
            Assert.True(launch.IsButtonDisabled);

            launch.NoteFeedback(id);
            launch.FinishAttempt();
            Assert.True(launch.IsButtonDisabled);

            launch.EndFeedback();
            Assert.False(launch.IsButtonDisabled);
            id++;
        }
    }

    [Fact]
    public void Feedback_requires_a_real_toast_id()
    {
        var launch = OpenInToolLaunch.Begin(FeatureOpenInTools.Explorer);

        Assert.Throws<ArgumentOutOfRangeException>(() => launch.NoteFeedback(0));
    }
}

public sealed class ToastServiceShownIdTests
{
    [Fact]
    public void Each_show_gets_its_own_id_and_hide_keeps_that_id()
    {
        var toast = new ToastService();
        Assert.Equal(0, toast.ShownId);

        toast.Show("Starting Explorer...");
        var first = toast.ShownId;
        Assert.True(toast.IsVisible);
        Assert.NotEqual(0, first);

        toast.Hide();
        Assert.False(toast.IsVisible);
        Assert.Equal(first, toast.ShownId);
        Assert.Null(toast.Message);

        toast.Show("Starting Explorer...");
        Assert.True(toast.IsVisible);
        Assert.NotEqual(first, toast.ShownId);
    }
}
