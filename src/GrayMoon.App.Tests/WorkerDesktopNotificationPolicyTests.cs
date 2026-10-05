using GrayMoon.App.Hubs;
using GrayMoon.App.Services;

namespace GrayMoon.App.Tests;

public sealed class WorkerDesktopNotificationPolicyTests
{
    [Fact]
    public void Startup_offline_does_not_notify()
    {
        var policy = new WorkerDesktopNotificationPolicy();

        Assert.Null(policy.OnChange(WorkerConnectionState.Offline, selfUpdateInProgress: false, workerSemVer: null));
    }

    [Fact]
    public void Version_mismatch_sends_update_required_warning()
    {
        var policy = new WorkerDesktopNotificationPolicy();

        var notification = policy.OnChange(WorkerConnectionState.VersionMismatch, selfUpdateInProgress: false, "1.0.0");

        Assert.NotNull(notification);
        Assert.Equal("GrayMoon Worker update required", notification.Title);
        Assert.Equal(DesktopNotificationSeverity.Warning, notification.Severity);
        Assert.Equal("/worker", notification.NavigationPath);
        Assert.Contains("1.0.0", notification.Message);
    }

    [Fact]
    public void Version_mismatch_during_self_update_does_not_resend_warning()
    {
        var policy = new WorkerDesktopNotificationPolicy();
        policy.OnChange(WorkerConnectionState.VersionMismatch, selfUpdateInProgress: false, "1.0.0");

        Assert.Null(policy.OnChange(WorkerConnectionState.VersionMismatch, selfUpdateInProgress: true, "1.0.0"));
    }

    [Fact]
    public void Offline_during_self_update_sends_installing_once()
    {
        var policy = new WorkerDesktopNotificationPolicy();
        policy.OnChange(WorkerConnectionState.VersionMismatch, selfUpdateInProgress: false, "1.0.0");
        policy.OnChange(WorkerConnectionState.VersionMismatch, selfUpdateInProgress: true, "1.0.0");

        var notification = policy.OnChange(WorkerConnectionState.Offline, selfUpdateInProgress: true, "1.0.0");

        Assert.NotNull(notification);
        Assert.Equal("GrayMoon Worker is installing", notification.Title);
        Assert.Equal(DesktopNotificationSeverity.Info, notification.Severity);
        Assert.Equal("/worker", notification.NavigationPath);

        Assert.Null(policy.OnChange(WorkerConnectionState.Offline, selfUpdateInProgress: true, "1.0.0"));
        Assert.Null(policy.OnChange(WorkerConnectionState.VersionMismatch, selfUpdateInProgress: true, "1.0.0"));
    }

    [Fact]
    public void Offline_after_being_connected_sends_error()
    {
        var policy = new WorkerDesktopNotificationPolicy();
        policy.OnChange(WorkerConnectionState.Online, selfUpdateInProgress: false, "1.0.0");

        var notification = policy.OnChange(WorkerConnectionState.Offline, selfUpdateInProgress: false, "1.0.0");

        Assert.NotNull(notification);
        Assert.Equal("GrayMoon Worker is offline", notification.Title);
        Assert.Equal(DesktopNotificationSeverity.Error, notification.Severity);
        Assert.Equal("/worker", notification.NavigationPath);
    }

    [Fact]
    public void Ending_self_update_while_still_offline_sends_offline_error()
    {
        var policy = new WorkerDesktopNotificationPolicy();
        policy.OnChange(WorkerConnectionState.VersionMismatch, selfUpdateInProgress: false, "1.0.0");
        policy.OnChange(WorkerConnectionState.VersionMismatch, selfUpdateInProgress: true, "1.0.0");
        policy.OnChange(WorkerConnectionState.Offline, selfUpdateInProgress: true, "1.0.0");

        var notification = policy.OnChange(WorkerConnectionState.Offline, selfUpdateInProgress: false, "1.0.0");

        Assert.NotNull(notification);
        Assert.Equal("GrayMoon Worker is offline", notification.Title);
        Assert.Equal(DesktopNotificationSeverity.Error, notification.Severity);
    }

    [Fact]
    public void Successful_reconnect_then_later_offline_sends_error_not_installing()
    {
        var policy = new WorkerDesktopNotificationPolicy();
        policy.OnChange(WorkerConnectionState.VersionMismatch, selfUpdateInProgress: false, "1.0.0");
        policy.OnChange(WorkerConnectionState.VersionMismatch, selfUpdateInProgress: true, "1.0.0");
        policy.OnChange(WorkerConnectionState.Offline, selfUpdateInProgress: true, "1.0.0");
        policy.OnChange(WorkerConnectionState.Online, selfUpdateInProgress: false, "2.0.0");

        var notification = policy.OnChange(WorkerConnectionState.Offline, selfUpdateInProgress: false, "2.0.0");

        Assert.NotNull(notification);
        Assert.Equal("GrayMoon Worker is offline", notification.Title);
        Assert.Equal(DesktopNotificationSeverity.Error, notification.Severity);
    }
}
