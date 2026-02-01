using GrayMoon.App.Services;
using Microsoft.AspNetCore.SignalR;

namespace GrayMoon.App.Hubs;

public sealed class AgentHub : Hub
{
    private readonly AgentConnectionTracker _connectionTracker;
    private readonly SyncCommandHandler _syncCommandHandler;

    public AgentHub(AgentConnectionTracker connectionTracker, SyncCommandHandler syncCommandHandler)
    {
        _connectionTracker = connectionTracker;
        _syncCommandHandler = syncCommandHandler;
    }

    public override async Task OnConnectedAsync()
    {
        _connectionTracker.OnAgentConnected(Context.ConnectionId);
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        _connectionTracker.OnAgentDisconnected(Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }

    /// <summary>Invoked by the agent when it completes a command. Delivers the response to the waiting caller.</summary>
    public Task ResponseCommand(string requestId, bool success, object? data, string? error)
    {
        AgentResponseDelivery.Complete(requestId, success, data, error);
        return Task.CompletedTask;
    }

    /// <summary>Invoked by the agent when a hook fires: agent ran GitVersion and pushes result for app to persist.</summary>
    public async Task SyncCommand(int workspaceId, int repositoryId, string version, string branch)
    {
        await _syncCommandHandler.HandleAsync(workspaceId, repositoryId, version, branch);
    }
}
