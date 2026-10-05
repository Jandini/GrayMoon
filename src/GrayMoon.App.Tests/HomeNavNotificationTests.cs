using GrayMoon.App.Services.Worker;
using GrayMoon.App.Services.Ui;

namespace GrayMoon.App.Tests;

public sealed class HomeNavNotificationTests
{
    [Theory]
    [InlineData(WorkerConnectionState.VersionMismatch, false, false, false, true)]
    [InlineData(WorkerConnectionState.VersionMismatch, true, false, false, false)]
    [InlineData(WorkerConnectionState.Offline, false, false, false, true)]
    [InlineData(WorkerConnectionState.Offline, true, false, false, false)]
    [InlineData(WorkerConnectionState.Online, false, false, false, false)]
    [InlineData(WorkerConnectionState.Online, false, true, false, true)]
    [InlineData(WorkerConnectionState.Online, true, true, false, false)]
    [InlineData(WorkerConnectionState.Connecting, false, false, false, false)]
    [InlineData(WorkerConnectionState.Online, false, false, true, true)]
    [InlineData(WorkerConnectionState.VersionMismatch, true, false, true, true)]
    public void ShouldShow_when_worker_or_connectors_need_attention(
        WorkerConnectionState state,
        bool selfUpdateInProgress,
        bool hostPrerequisitesMissing,
        bool connectorsRequired,
        bool expected)
    {
        Assert.Equal(
            expected,
            HomeNavNotification.ShouldShow(state, selfUpdateInProgress, hostPrerequisitesMissing, connectorsRequired));
    }

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    public void ConnectorsRequired_matches_home_connector_button(
        bool hasConnectors,
        bool anyUsedConnectorUnhealthy,
        bool expected)
    {
        Assert.Equal(expected, HomeNavNotification.ConnectorsRequired(hasConnectors, anyUsedConnectorUnhealthy));
    }

    [Fact]
    public void Title_names_each_worker_reason()
    {
        Assert.Equal(
            "Worker update available",
            HomeNavNotification.Title(WorkerConnectionState.VersionMismatch, false, false, true, false));
        Assert.Equal(
            "Worker installation required",
            HomeNavNotification.Title(WorkerConnectionState.Offline, false, false, true, false));
        Assert.Equal(
            "Worker prerequisites required",
            HomeNavNotification.Title(WorkerConnectionState.Online, false, true, true, false));
    }

    [Fact]
    public void Title_hides_worker_reason_during_self_update_and_keeps_connectors()
    {
        Assert.Equal(
            "Connector required",
            HomeNavNotification.Title(WorkerConnectionState.Offline, true, false, false, false));
        Assert.Equal(
            "",
            HomeNavNotification.Title(WorkerConnectionState.VersionMismatch, true, false, true, false));
    }

    [Fact]
    public void Title_joins_worker_and_connector_reasons()
    {
        Assert.Equal(
            "Worker installation required. Connector required",
            HomeNavNotification.Title(WorkerConnectionState.Offline, false, false, false, false));
        Assert.Equal(
            "Worker prerequisites required. Unhealthy connector in use",
            HomeNavNotification.Title(WorkerConnectionState.Online, false, true, true, true));
    }
}
