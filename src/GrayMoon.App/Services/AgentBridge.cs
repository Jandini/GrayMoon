using System.Text.Json;
using GrayMoon.Abstractions.Agent;
using GrayMoon.App.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;

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
    AgentCommandCancelSender cancelSender,
    IOptions<AgentBridgeOptions> options,
    ILogger<AgentBridge> logger) : IAgentBridge
{
    private readonly TimeSpan _commandTimeout = TimeSpan.FromSeconds(Math.Max(1, options.Value.CommandTimeoutSeconds));

    public bool IsAgentConnected => connectionTracker.GetAgentConnectionId() != null;

    public async Task<AgentCommandResponse> SendCommandAsync(string command, object args, CancellationToken cancellationToken = default)
    {
        var connectionId = connectionTracker.GetAgentConnectionId();
        if (string.IsNullOrEmpty(connectionId))
            return new AgentCommandResponse(false, null, "Agent not connected. Start GrayMoon.Agent to sync repositories.");

        var requestId = Guid.NewGuid().ToString("N");
        var argsJson = args != null ? JsonSerializer.SerializeToElement(args) : (JsonElement?)null;

        var sink = TerminalSinkContext.Current;
        Action<AgentCommandStreamLine>? onLine = sink != null ? sink.Append : null;

        using var timeoutCts = new CancellationTokenSource(_commandTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        var task = AgentResponseDelivery.WaitAsync(requestId, linkedCts.Token, onLine);

        try
        {
            await hubContext.Clients.Client(connectionId).SendAsync(AgentHubMethods.RequestCommand, requestId, command, argsJson, cancellationToken);
            logger.LogDebug("Sent RequestCommand: {RequestId}, {Command}", requestId, command);
            return await task;
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // Overall command timeout, not a caller-initiated cancellation: notify the agent to stop
            // work on this request, then fail fast with a normal response instead of throwing - callers
            // across the app already handle a false/error AgentCommandResponse, so no call-site changes
            // are needed to get this "fail fast, let the user retry" behavior everywhere.
            cancelSender.NotifyCancel(requestId);
            logger.LogWarning(
                "Agent command {Command} ({RequestId}) timed out after {TimeoutSeconds}s waiting for a response",
                command, requestId, _commandTimeout.TotalSeconds);
            return new AgentCommandResponse(false, null, $"Agent command timed out after {_commandTimeout.TotalSeconds:0}s.");
        }
        catch (OperationCanceledException)
        {
            // WaitAsync cancel registration also notifies the agent; send here too in case
            // RequestCommand was delivered before SendAsync observed cancellation.
            cancelSender.NotifyCancel(requestId);
            throw;
        }
        catch (Exception ex)
        {
            AgentResponseDelivery.Fail(requestId, ex);
            logger.LogError(ex, "Failed to send command {Command} to agent", command);
            return new AgentCommandResponse(false, null, ex.Message);
        }
    }
}
