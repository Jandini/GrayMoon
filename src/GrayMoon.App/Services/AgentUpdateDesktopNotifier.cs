using GrayMoon.App.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GrayMoon.App.Services;

/// <summary>
/// Pushes Windows desktop notifications for agent lifecycle events that the in-app badge
/// alone can miss (window unfocused, user on another page). Version mismatch, an in-progress
/// self-update disconnect, and a genuine offline are distinct events - the policy decides
/// which one to send so an upgrade never looks like a surprise failure.
/// Clicking the notification (handled on the GrayMoon.Desktop side) opens the Agent page.
/// Best-effort - <see cref="IHubContext{DesktopNotificationHub}"/> silently sends to no one
/// when GrayMoon.Desktop is not connected (e.g. running in the browser, not desktop mode).
/// </summary>
public sealed class AgentUpdateDesktopNotifier(
    AgentConnectionTracker agentConnectionTracker,
    IHubContext<DesktopNotificationHub> desktopHub,
    ILogger<AgentUpdateDesktopNotifier> logger) : IHostedService
{
    private readonly AgentDesktopNotificationPolicy _policy = new();

    public Task StartAsync(CancellationToken cancellationToken)
    {
        agentConnectionTracker.OnStateChanged(OnAgentStateChanged);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private void OnAgentStateChanged(AgentConnectionState state)
    {
        var notification = _policy.OnChange(
            state,
            agentConnectionTracker.IsSelfUpdateInProgress,
            agentConnectionTracker.AgentSemVer);
        if (notification is null)
            return;

        _ = SendAsync(notification);
    }

    private async Task SendAsync(DesktopNotification notification)
    {
        try
        {
            await desktopHub.Clients.All.SendAsync("Notify", notification);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to push agent desktop notification ({Title})", notification.Title);
        }
    }
}
