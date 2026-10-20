using GrayMoon.App.Services.Worker;
using GrayMoon.App.Services.Ui;

namespace GrayMoon.App.Tests;

public sealed class HomeNavNotificationTests
{
    [Theory]
    [InlineData(WorkerConnectionState.Offline, false, true)]
    [InlineData(WorkerConnectionState.Offline, true, false)]
    [InlineData(WorkerConnectionState.Online, false, false)]
    [InlineData(WorkerConnectionState.Online, true, false)]
    [InlineData(WorkerConnectionState.VersionMismatch, false, false)]
    [InlineData(WorkerConnectionState.Connecting, false, false)]
    public void Circle_is_red_only_while_the_worker_is_down(
        WorkerConnectionState state,
        bool selfUpdateInProgress,
        bool expected)
    {
        Assert.Equal(expected, HomeNavNotification.ShouldShow(state, selfUpdateInProgress));
    }

    [Fact]
    public void Title_names_a_down_worker_and_is_empty_otherwise()
    {
        Assert.Equal(
            "Worker installation required",
            HomeNavNotification.Title(WorkerConnectionState.Offline, selfUpdateInProgress: false));
        Assert.Equal(
            "",
            HomeNavNotification.Title(WorkerConnectionState.Online, selfUpdateInProgress: false));
        Assert.Equal(
            "",
            HomeNavNotification.Title(WorkerConnectionState.Offline, selfUpdateInProgress: true));
    }
}
