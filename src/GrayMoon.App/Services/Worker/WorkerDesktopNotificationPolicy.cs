using GrayMoon.App.Hubs;

namespace GrayMoon.App.Services.Worker;

/// <summary>
/// Decides which Windows desktop notification (if any) to send for a worker connection
/// change. Knows about in-progress self-updates so a disconnect during install is reported
/// as installing, not as an unexpected offline or a second update-required warning.
/// </summary>
internal sealed class WorkerDesktopNotificationPolicy
{
    private bool _hasBeenConnected;
    private bool _selfUpdateInProgress;
    private bool _installingNotified;
    private bool _logonPasswordNotified;

    public DesktopNotification? OnChange(
        WorkerConnectionState state,
        bool selfUpdateInProgress,
        string? workerSemVer,
        bool logonPasswordRequired = false)
    {
        if (selfUpdateInProgress && !_selfUpdateInProgress)
            _installingNotified = false;
        _selfUpdateInProgress = selfUpdateInProgress;

        if (state is WorkerConnectionState.Online or WorkerConnectionState.VersionMismatch)
        {
            _hasBeenConnected = true;
            _logonPasswordNotified = false;
        }

        if (logonPasswordRequired && !selfUpdateInProgress && state == WorkerConnectionState.Offline)
        {
            if (_logonPasswordNotified)
                return null;
            _logonPasswordNotified = true;
            return LogonPasswordRequired();
        }

        if (state == WorkerConnectionState.VersionMismatch && !selfUpdateInProgress)
            return UpdateRequired(workerSemVer);

        if (state == WorkerConnectionState.Offline)
        {
            if (selfUpdateInProgress)
            {
                if (_installingNotified)
                    return null;
                _installingNotified = true;
                return Installing();
            }

            if (_hasBeenConnected)
                return Offline();
        }

        return null;
    }

    internal static DesktopNotification UpdateRequired(string? workerSemVer) =>
        new(
            Guid.NewGuid().ToString(),
            "GrayMoon Worker update required",
            $"The GrayMoon Worker (version {workerSemVer ?? "unknown"}) is out of date. Click to update now.",
            DesktopNotificationSeverity.Warning,
            "/worker",
            DateTimeOffset.UtcNow);

    internal static DesktopNotification Installing() =>
        new(
            Guid.NewGuid().ToString(),
            "GrayMoon Worker is installing",
            "The GrayMoon Worker is updating and will reconnect when the install finishes.",
            DesktopNotificationSeverity.Info,
            "/worker",
            DateTimeOffset.UtcNow);

    internal static DesktopNotification LogonPasswordRequired() =>
        new(
            Guid.NewGuid().ToString(),
            "GrayMoon Worker install failed",
            "The Windows password for the Worker service is no longer valid. Open the Worker page and install it again.",
            DesktopNotificationSeverity.Error,
            "/worker",
            DateTimeOffset.UtcNow);

    internal static DesktopNotification Offline() =>
        new(
            Guid.NewGuid().ToString(),
            "GrayMoon Worker is offline",
            "The GrayMoon Worker disconnected. Git and filesystem operations are unavailable until it reconnects.",
            DesktopNotificationSeverity.Error,
            "/worker",
            DateTimeOffset.UtcNow);
}
