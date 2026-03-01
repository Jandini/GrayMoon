using GrayMoon.App.Services;
using Microsoft.AspNetCore.SignalR;

namespace GrayMoon.App.Hubs;

public sealed class AgentHub(AgentConnectionTracker connectionTracker, SyncCommandHandler syncCommandHandler, IServiceScopeFactory scopeFactory) : Hub
{
    public override async Task OnConnectedAsync()
    {
        connectionTracker.OnAgentConnected(Context.ConnectionId);
        // Refresh workspace root from settings when agent connects.
        // Use a new scope so the DbContext isn't disposed before the delay completes.
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500), CancellationToken.None);
                await using var scope = scopeFactory.CreateAsyncScope();
                var workspaceService = scope.ServiceProvider.GetRequiredService<WorkspaceService>();
                await workspaceService.RefreshRootPathAsync(CancellationToken.None);
            }
            catch
            {
                // Ignore errors in background task
            }
        });
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        connectionTracker.OnAgentDisconnected(Context.ConnectionId);
        // Clear cached workspace root when agent disconnects.
        // WorkspaceService is scoped, so resolve it from a fresh scope.
        await using var scope = scopeFactory.CreateAsyncScope();
        var workspaceService = scope.ServiceProvider.GetRequiredService<WorkspaceService>();
        workspaceService.ClearCachedRootPath();
        await base.OnDisconnectedAsync(exception);
    }

    /// <summary>Invoked by the agent when it completes a command. Delivers the response to the waiting caller.</summary>
    public Task ResponseCommand(string requestId, bool success, object? data, string? error)
    {
        AgentResponseDelivery.Complete(requestId, success, data, error);
        return Task.CompletedTask;
    }

    /// <summary>Invoked by the agent when a hook fires: agent ran GitVersion (and fetch/commit counts) and pushes result for app to persist.</summary>
    public async Task SyncCommand(int workspaceId, int repositoryId, string version, string branch, int? outgoingCommits = null, int? incomingCommits = null, bool? hasUpstream = null)
    {
        await syncCommandHandler.HandleAsync(workspaceId, repositoryId, version, branch, outgoingCommits, incomingCommits, hasUpstream);
    }

    /// <summary>Invoked by the agent when it connects to report its SemVer version.</summary>
    public Task ReportSemVer(string semVer)
    {
        connectionTracker.ReportAgentSemVer(Context.ConnectionId, semVer);
        return Task.CompletedTask;
    }
}
