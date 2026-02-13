using GrayMoon.App.Services;
using Microsoft.AspNetCore.SignalR;

namespace GrayMoon.App.Hubs;

public sealed class AgentHub(AgentConnectionTracker connectionTracker, SyncCommandHandler syncCommandHandler) : Hub
{
    public override async Task OnConnectedAsync()
    {
        connectionTracker.OnAgentConnected(Context.ConnectionId);
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        connectionTracker.OnAgentDisconnected(Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }

    /// <summary>Invoked by the agent when it completes a command. Delivers the response to the waiting caller.</summary>
    public Task ResponseCommand(string requestId, bool success, object? data, string? error)
    {
        AgentResponseDelivery.Complete(requestId, success, data, error);
        return Task.CompletedTask;
    }

    /// <summary>Invoked by the agent when a hook fires: agent ran GitVersion (and fetch/commit counts) and pushes result for app to persist.</summary>
    public async Task SyncCommand(int workspaceId, int repositoryId, string version, string branch, int? outgoingCommits = null, int? incomingCommits = null)
    {
        await syncCommandHandler.HandleAsync(workspaceId, repositoryId, version, branch, outgoingCommits, incomingCommits);
    }

    /// <summary>Invoked by the agent when it connects to report its SemVer version.</summary>
    public Task ReportSemVer(string semVer)
    {
        connectionTracker.ReportAgentSemVer(Context.ConnectionId, semVer);
        return Task.CompletedTask;
    }
}
