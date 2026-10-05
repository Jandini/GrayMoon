using GrayMoon.App.Services;

namespace GrayMoon.App.Tests;

public sealed class WorkerConnectionTrackerTests
{
    [Fact]
    public void BeginSelfUpdate_survives_disconnect_and_stale_reconnect_until_matching_version()
    {
        var tracker = new WorkerConnectionTracker("2.0.0");
        tracker.OnWorkerConnected("old");
        tracker.ReportWorkerSemVer("old", "1.0.0");
        Assert.Equal(WorkerConnectionState.VersionMismatch, tracker.State);

        tracker.BeginSelfUpdate();
        Assert.True(tracker.IsSelfUpdateInProgress);

        tracker.OnWorkerDisconnected("old");
        Assert.Equal(WorkerConnectionState.Offline, tracker.State);
        Assert.True(tracker.IsSelfUpdateInProgress);

        // Installer can restart the old binary before the new files are in place.
        tracker.OnWorkerConnected("old-again");
        tracker.ReportWorkerSemVer("old-again", "1.0.0");
        Assert.Equal(WorkerConnectionState.VersionMismatch, tracker.State);
        Assert.True(tracker.IsSelfUpdateInProgress);

        tracker.OnWorkerDisconnected("old-again");
        tracker.OnWorkerConnected("new");
        Assert.Equal(WorkerConnectionState.Online, tracker.State);
        Assert.True(tracker.IsSelfUpdateInProgress);

        tracker.ReportWorkerSemVer("new", "2.0.0");
        Assert.Equal(WorkerConnectionState.Online, tracker.State);
        Assert.False(tracker.IsSelfUpdateInProgress);
    }

    [Fact]
    public void EndSelfUpdate_clears_in_progress_without_changing_connection_state()
    {
        var tracker = new WorkerConnectionTracker("2.0.0");
        tracker.OnWorkerConnected("old");
        tracker.ReportWorkerSemVer("old", "1.0.0");
        tracker.BeginSelfUpdate();
        tracker.OnWorkerDisconnected("old");

        var raised = new List<WorkerConnectionState>();
        tracker.OnStateChanged(raised.Add);
        raised.Clear();

        tracker.EndSelfUpdate();

        Assert.False(tracker.IsSelfUpdateInProgress);
        Assert.Equal(WorkerConnectionState.Offline, tracker.State);
        Assert.Equal(WorkerConnectionState.Offline, Assert.Single(raised));
    }

    [Fact]
    public void BeginSelfUpdate_from_a_state_handler_does_not_deadlock()
    {
        var tracker = new WorkerConnectionTracker("1.0.0");
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
