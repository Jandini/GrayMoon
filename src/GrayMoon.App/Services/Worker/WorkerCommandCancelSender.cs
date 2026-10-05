using GrayMoon.Abstractions.Worker;
using GrayMoon.App.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace GrayMoon.App.Services.Worker;

/// <summary>
/// Fire-and-forget CancelCommand to the connected worker when an App-side wait is aborted.
/// Registers itself with <see cref="WorkerResponseDelivery"/> on construction.
/// </summary>
public sealed class WorkerCommandCancelSender(
    IHubContext<WorkerHub> hubContext,
    WorkerConnectionTracker connectionTracker,
    ILogger<WorkerCommandCancelSender> logger)
{
    public void NotifyCancel(string requestId)
    {
        if (string.IsNullOrEmpty(requestId))
            return;

        _ = NotifyCancelAsync(requestId);
    }

    private async Task NotifyCancelAsync(string requestId)
    {
        var connectionId = connectionTracker.GetWorkerConnectionId();
        if (string.IsNullOrEmpty(connectionId))
            return;

        try
        {
            await hubContext.Clients.Client(connectionId)
                .SendAsync(WorkerHubMethods.CancelCommand, requestId, CancellationToken.None);
            logger.LogDebug("Sent CancelCommand: {RequestId}", requestId);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to send CancelCommand for {RequestId}", requestId);
        }
    }
}
