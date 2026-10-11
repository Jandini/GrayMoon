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
    public void ReportLogonPasswordFailure_ends_installing_and_clears_after_the_worker_returns()
    {
        var tracker = new WorkerConnectionTracker("2.0.0");
        tracker.OnWorkerConnected("old");
        tracker.ReportWorkerSemVer("old", "1.0.0");
        tracker.BeginSelfUpdate();
        tracker.OnWorkerDisconnected("old");
        Assert.True(tracker.IsSelfUpdateInProgress);

        var raised = new List<WorkerConnectionState>();
        tracker.OnStateChanged(raised.Add);
        raised.Clear();

        tracker.ReportLogonPasswordFailure();

        Assert.True(tracker.LogonPasswordRequired);
        Assert.False(tracker.IsSelfUpdateInProgress);
        Assert.Equal(WorkerConnectionState.Offline, Assert.Single(raised));

        tracker.ReportLogonPasswordFailure();
        Assert.True(tracker.LogonPasswordRequired);

        tracker.OnWorkerConnected("new");
        tracker.ReportWorkerSemVer("new", "2.0.0");
        Assert.False(tracker.LogonPasswordRequired);
        Assert.Equal(WorkerConnectionState.Online, tracker.State);
    }

    [Fact]
    public void ReportLogonPasswordFailure_ignores_a_worker_that_is_not_updating()
    {
        var tracker = new WorkerConnectionTracker("2.0.0");
        tracker.OnWorkerConnected("w");
        tracker.ReportWorkerSemVer("w", "2.0.0");

        tracker.ReportLogonPasswordFailure();

        Assert.False(tracker.LogonPasswordRequired);
        Assert.Equal(WorkerConnectionState.Online, tracker.State);
    }

    [Fact]
    public void ReportWorkerSemVer_notifies_when_a_matching_version_leaves_the_state_online()
    {
        var tracker = new WorkerConnectionTracker("1.0.0");
        var versions = new List<string?>();
        tracker.OnStateChanged(_ => versions.Add(tracker.WorkerSemVer));
        versions.Clear();

        tracker.OnWorkerConnected("w");
        tracker.ReportWorkerSemVer("w", "1.0.0");

        Assert.Equal(WorkerConnectionState.Online, tracker.State);
        Assert.Equal(new string?[] { null, "1.0.0" }, versions);
    }

    [Fact]
    public void ReportWorkerSemVer_mismatch_is_a_single_transition_off_the_unversioned_online()
    {
        var tracker = new WorkerConnectionTracker("1.0.0");
        var raised = new List<WorkerConnectionState>();
        tracker.OnStateChanged(raised.Add);
        raised.Clear();

        tracker.OnWorkerConnected("w");
        tracker.ReportWorkerSemVer("w", "0.9.0");

        Assert.Equal(WorkerConnectionState.VersionMismatch, tracker.State);
        Assert.Equal(
            [WorkerConnectionState.Online, WorkerConnectionState.VersionMismatch],
            raised);
    }

    [Fact]
    public void ReportWorkerSemVer_same_matching_version_again_does_not_notify()
    {
        var tracker = new WorkerConnectionTracker("1.0.0");
        tracker.OnWorkerConnected("w");
        tracker.ReportWorkerSemVer("w", "1.0.0");

        var raised = new List<WorkerConnectionState>();
        tracker.OnStateChanged(raised.Add);
        raised.Clear();

        tracker.ReportWorkerSemVer("w", "1.0.0");

        Assert.Empty(raised);
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

        Assert.True(started.Wait(TimeSpan.FromSeconds(30)));
        Assert.True(tracker.IsSelfUpdateInProgress);
    }
}
