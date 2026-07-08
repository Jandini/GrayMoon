using GrayMoon.Abstractions.Agent;
using GrayMoon.App.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace GrayMoon.App.Services;

/// <summary>
/// Fire-and-forget CancelCommand to the connected agent when an App-side wait is aborted.
/// Registers itself with <see cref="AgentResponseDelivery"/> on construction.
/// </summary>
public sealed class AgentCommandCancelSender(
    IHubContext<AgentHub> hubContext,
    AgentConnectionTracker connectionTracker,
    ILogger<AgentCommandCancelSender> logger)
{
    public void NotifyCancel(string requestId)
    {
        if (string.IsNullOrEmpty(requestId))
            return;

        _ = NotifyCancelAsync(requestId);
    }

    private async Task NotifyCancelAsync(string requestId)
    {
        var connectionId = connectionTracker.GetAgentConnectionId();
        if (string.IsNullOrEmpty(connectionId))
            return;

        try
        {
            await hubContext.Clients.Client(connectionId)
                .SendAsync(AgentHubMethods.CancelCommand, requestId, CancellationToken.None);
            logger.LogDebug("Sent CancelCommand: {RequestId}", requestId);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to send CancelCommand for {RequestId}", requestId);
        }
    }
}
