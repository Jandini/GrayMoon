using System.Text.Json;
using GrayMoon.App.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace GrayMoon.App.Services;

/// <summary>Sends commands to the agent via SignalR and awaits responses.</summary>
public interface IAgentBridge
{
    bool IsAgentConnected { get; }
    Task<AgentCommandResponse> SendCommandAsync(string command, object args, CancellationToken cancellationToken = default);
}

public sealed class AgentBridge(
    IHubContext<AgentHub> hubContext,
    AgentConnectionTracker connectionTracker,
    ILogger<AgentBridge> logger) : IAgentBridge
{
    public bool IsAgentConnected => connectionTracker.GetAgentConnectionId() != null;

    public async Task<AgentCommandResponse> SendCommandAsync(string command, object args, CancellationToken cancellationToken = default)
    {
        var connectionId = connectionTracker.GetAgentConnectionId();
        if (string.IsNullOrEmpty(connectionId))
            return new AgentCommandResponse(false, null, "Agent not connected. Start GrayMoon.Agent to sync repositories.");

        var requestId = Guid.NewGuid().ToString("N");
        var argsJson = args != null ? JsonSerializer.SerializeToElement(args) : (JsonElement?)null;

        var task = AgentResponseDelivery.WaitAsync(requestId, cancellationToken);

        try
        {
            await hubContext.Clients.Client(connectionId).SendAsync("RequestCommand", requestId, command, argsJson, cancellationToken);
            logger.LogDebug("Sent RequestCommand: {RequestId}, {Command}", requestId, command);
            return await task;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to send command {Command} to agent", command);
            return new AgentCommandResponse(false, null, ex.Message);
        }
    }
}
