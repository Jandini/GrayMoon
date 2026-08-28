using GrayMoon.App.Services;

namespace GrayMoon.App.Tests;

public sealed class AgentConnectionTrackerTests
{
    [Fact]
    public void BeginSelfUpdate_survives_disconnect_and_stale_reconnect_until_matching_version()
    {
        var tracker = new AgentConnectionTracker("2.0.0");
        tracker.OnAgentConnected("old");
        tracker.ReportAgentSemVer("old", "1.0.0");
        Assert.Equal(AgentConnectionState.VersionMismatch, tracker.State);

        tracker.BeginSelfUpdate();
        Assert.True(tracker.IsSelfUpdateInProgress);

        tracker.OnAgentDisconnected("old");
        Assert.Equal(AgentConnectionState.Offline, tracker.State);
        Assert.True(tracker.IsSelfUpdateInProgress);

        // Installer can restart the old binary before the new files are in place.
        tracker.OnAgentConnected("old-again");
        tracker.ReportAgentSemVer("old-again", "1.0.0");
        Assert.Equal(AgentConnectionState.VersionMismatch, tracker.State);
        Assert.True(tracker.IsSelfUpdateInProgress);

        tracker.OnAgentDisconnected("old-again");
        tracker.OnAgentConnected("new");
        Assert.Equal(AgentConnectionState.Online, tracker.State);
        Assert.True(tracker.IsSelfUpdateInProgress);

        tracker.ReportAgentSemVer("new", "2.0.0");
        Assert.Equal(AgentConnectionState.Online, tracker.State);
        Assert.False(tracker.IsSelfUpdateInProgress);
    }

    [Fact]
    public void EndSelfUpdate_clears_in_progress_without_changing_connection_state()
    {
        var tracker = new AgentConnectionTracker("2.0.0");
        tracker.OnAgentConnected("old");
        tracker.ReportAgentSemVer("old", "1.0.0");
        tracker.BeginSelfUpdate();
        tracker.OnAgentDisconnected("old");

        var raised = new List<AgentConnectionState>();
        tracker.OnStateChanged(raised.Add);
        raised.Clear();

        tracker.EndSelfUpdate();

        Assert.False(tracker.IsSelfUpdateInProgress);
        Assert.Equal(AgentConnectionState.Offline, tracker.State);
        Assert.Equal(AgentConnectionState.Offline, Assert.Single(raised));
    }

    [Fact]
    public void BeginSelfUpdate_from_a_state_handler_does_not_deadlock()
    {
        var tracker = new AgentConnectionTracker("1.0.0");
        var started = new ManualResetEventSlim(false);
        tracker.OnStateChanged(_ =>
        {
            tracker.BeginSelfUpdate();
            started.Set();
        });

        Assert.True(started.Wait(TimeSpan.FromSeconds(2)));
        Assert.True(tracker.IsSelfUpdateInProgress);
    }
}
