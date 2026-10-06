using GrayMoon.App.Api.Endpoints;
using GrayMoon.App.Services.Worker;
using Microsoft.AspNetCore.Http;

namespace GrayMoon.App.Tests;

public sealed class WorkerInstallFailureTests
{
    [Fact]
    public void Report_during_self_update_ends_installing_and_requires_the_password()
    {
        var tracker = new WorkerConnectionTracker("2.0.0");
        tracker.OnWorkerConnected("old");
        tracker.BeginSelfUpdate();
        tracker.OnWorkerDisconnected("old");

        var result = WorkerEndpoints.ReportInstallFailure(
            new DefaultHttpContext().Request,
            new WorkerEndpoints.WorkerInstallFailureRequest(WorkerEndpoints.LogonPasswordFailureReason),
            tracker);

        Assert.Equal(StatusCodes.Status200OK, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
        Assert.True(tracker.LogonPasswordRequired);
        Assert.False(tracker.IsSelfUpdateInProgress);
        Assert.Equal("error", WorkerStatusText.Label(tracker.IsSelfUpdateInProgress, tracker.LogonPasswordRequired, tracker.State, pendingCount: 0));
    }

    [Fact]
    public void Report_is_ignored_when_no_update_is_running()
    {
        var tracker = new WorkerConnectionTracker("2.0.0");
        tracker.OnWorkerConnected("w");
        tracker.ReportWorkerSemVer("w", "2.0.0");

        WorkerEndpoints.ReportInstallFailure(
            new DefaultHttpContext().Request,
            new WorkerEndpoints.WorkerInstallFailureRequest(WorkerEndpoints.LogonPasswordFailureReason),
            tracker);

        Assert.False(tracker.LogonPasswordRequired);
        Assert.Equal("online", WorkerStatusText.Label(false, false, tracker.State, pendingCount: 0));
    }

    [Fact]
    public void Browser_origin_and_unknown_reason_are_rejected()
    {
        var tracker = new WorkerConnectionTracker("2.0.0");
        tracker.BeginSelfUpdate();

        var browser = new DefaultHttpContext();
        browser.Request.Headers.Origin = "http://localhost";
        var forbidden = WorkerEndpoints.ReportInstallFailure(
            browser.Request,
            new WorkerEndpoints.WorkerInstallFailureRequest(WorkerEndpoints.LogonPasswordFailureReason),
            tracker);
        Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsAssignableFrom<IStatusCodeHttpResult>(forbidden).StatusCode);
        Assert.True(tracker.IsSelfUpdateInProgress);
        Assert.False(tracker.LogonPasswordRequired);

        var unknown = WorkerEndpoints.ReportInstallFailure(
            new DefaultHttpContext().Request,
            new WorkerEndpoints.WorkerInstallFailureRequest("disk-full"),
            tracker);
        Assert.Equal(StatusCodes.Status400BadRequest, Assert.IsAssignableFrom<IStatusCodeHttpResult>(unknown).StatusCode);
        Assert.True(tracker.IsSelfUpdateInProgress);
    }

    [Fact]
    public void Badge_stays_installing_until_the_password_failure_is_reported()
    {
        Assert.Equal("installing", WorkerStatusText.Label(selfUpdateInProgress: true, logonPasswordRequired: false, WorkerConnectionState.Offline, 0));
        Assert.Equal(
            "error",
            WorkerStatusText.Label(selfUpdateInProgress: false, logonPasswordRequired: true, WorkerConnectionState.Offline, 0));
        Assert.Contains(
            "password",
            WorkerStatusText.Title(false, true, WorkerConnectionState.Offline, null, 0, false),
            StringComparison.OrdinalIgnoreCase);
    }
}
