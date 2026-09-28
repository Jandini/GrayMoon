using GrayMoon.App.Services.Agent;
using GrayMoon.App.Services.Ui;

namespace GrayMoon.App.Tests;

public sealed class HomeNavNotificationTests
{
    [Theory]
    [InlineData(AgentConnectionState.VersionMismatch, false, false, false, true)]
    [InlineData(AgentConnectionState.VersionMismatch, true, false, false, false)]
    [InlineData(AgentConnectionState.Offline, false, false, false, true)]
    [InlineData(AgentConnectionState.Offline, true, false, false, false)]
    [InlineData(AgentConnectionState.Online, false, false, false, false)]
    [InlineData(AgentConnectionState.Online, false, true, false, true)]
    [InlineData(AgentConnectionState.Online, true, true, false, false)]
    [InlineData(AgentConnectionState.Connecting, false, false, false, false)]
    [InlineData(AgentConnectionState.Online, false, false, true, true)]
    [InlineData(AgentConnectionState.VersionMismatch, true, false, true, true)]
    public void ShouldShow_when_worker_or_connectors_need_attention(
        AgentConnectionState state,
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
            HomeNavNotification.Title(AgentConnectionState.VersionMismatch, false, false, true, false));
        Assert.Equal(
            "Worker installation required",
            HomeNavNotification.Title(AgentConnectionState.Offline, false, false, true, false));
        Assert.Equal(
            "Worker prerequisites required",
            HomeNavNotification.Title(AgentConnectionState.Online, false, true, true, false));
    }

    [Fact]
    public void Title_hides_worker_reason_during_self_update_and_keeps_connectors()
    {
        Assert.Equal(
            "Connector required",
            HomeNavNotification.Title(AgentConnectionState.Offline, true, false, false, false));
        Assert.Equal(
            "",
            HomeNavNotification.Title(AgentConnectionState.VersionMismatch, true, false, true, false));
    }

    [Fact]
    public void Title_joins_worker_and_connector_reasons()
    {
        Assert.Equal(
            "Worker installation required. Connector required",
            HomeNavNotification.Title(AgentConnectionState.Offline, false, false, false, false));
        Assert.Equal(
            "Worker prerequisites required. Unhealthy connector in use",
            HomeNavNotification.Title(AgentConnectionState.Online, false, true, true, true));
    }
}
