using GrayMoon.App.Hubs;

namespace GrayMoon.App.Services.Agent;

/// <summary>
/// Decides which Windows desktop notification (if any) to send for an agent connection
/// change. Knows about in-progress self-updates so a disconnect during install is reported
/// as installing, not as an unexpected offline or a second update-required warning.
/// </summary>
internal sealed class AgentDesktopNotificationPolicy
{
    private bool _hasBeenConnected;
    private bool _selfUpdateInProgress;
    private bool _installingNotified;

    public DesktopNotification? OnChange(AgentConnectionState state, bool selfUpdateInProgress, string? agentSemVer)
    {
        if (selfUpdateInProgress && !_selfUpdateInProgress)
            _installingNotified = false;
        _selfUpdateInProgress = selfUpdateInProgress;

        if (state is AgentConnectionState.Online or AgentConnectionState.VersionMismatch)
            _hasBeenConnected = true;

        if (state == AgentConnectionState.VersionMismatch && !selfUpdateInProgress)
            return UpdateRequired(agentSemVer);

        if (state == AgentConnectionState.Offline)
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

    internal static DesktopNotification UpdateRequired(string? agentSemVer) =>
        new(
            Guid.NewGuid().ToString(),
            "GrayMoon Worker update required",
            $"The GrayMoon Worker (version {agentSemVer ?? "unknown"}) is out of date. Click to update now.",
            DesktopNotificationSeverity.Warning,
            "/agent",
            DateTimeOffset.UtcNow);

    internal static DesktopNotification Installing() =>
        new(
            Guid.NewGuid().ToString(),
            "GrayMoon Worker is installing",
            "The GrayMoon Worker is updating and will reconnect when the install finishes.",
            DesktopNotificationSeverity.Info,
            "/agent",
            DateTimeOffset.UtcNow);

    internal static DesktopNotification Offline() =>
        new(
            Guid.NewGuid().ToString(),
            "GrayMoon Worker is offline",
            "The GrayMoon Worker disconnected. Git and filesystem operations are unavailable until it reconnects.",
            DesktopNotificationSeverity.Error,
            "/agent",
            DateTimeOffset.UtcNow);
}
