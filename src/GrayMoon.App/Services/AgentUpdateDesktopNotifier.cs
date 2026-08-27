using GrayMoon.App.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GrayMoon.App.Services;

/// <summary>
/// Pushes a Windows desktop notification the moment the agent connection reports a version
/// mismatch, so a stale agent is surfaced immediately instead of only via the in-app badge on
/// the Agent page. Clicking the notification (handled on the GrayMoon.Desktop side) opens the
/// Agent page. Best-effort - <see cref="IHubContext{DesktopNotificationHub}"/> silently sends to
/// no one when GrayMoon.Desktop is not connected (e.g. running in the browser, not desktop mode).
/// </summary>
public sealed class AgentUpdateDesktopNotifier(
    AgentConnectionTracker agentConnectionTracker,
    IHubContext<DesktopNotificationHub> desktopHub,
    ILogger<AgentUpdateDesktopNotifier> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        agentConnectionTracker.OnStateChanged(OnAgentStateChanged);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private void OnAgentStateChanged(AgentConnectionState state)
    {
        if (state != AgentConnectionState.VersionMismatch)
            return;

        _ = NotifyAsync();
    }

    private async Task NotifyAsync()
    {
        var agentSemVer = agentConnectionTracker.AgentSemVer ?? "unknown";
        var notification = new DesktopNotification(
            Guid.NewGuid().ToString(),
            "GrayMoon Agent update required",
            $"The GrayMoon Agent (version {agentSemVer}) is out of date. Click to update now.",
            DesktopNotificationSeverity.Warning,
            "/agent",
            DateTimeOffset.UtcNow);

        try
        {
            await desktopHub.Clients.All.SendAsync("Notify", notification);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to push agent update desktop notification");
        }
    }
}
