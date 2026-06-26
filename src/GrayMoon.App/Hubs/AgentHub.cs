using GrayMoon.Abstractions.Agent;
using GrayMoon.Abstractions.Notifications;
using GrayMoon.App.Services;
using Microsoft.AspNetCore.SignalR;

namespace GrayMoon.App.Hubs;

public sealed class AgentHub(
    AgentConnectionTracker connectionTracker,
    AgentQueueStateService agentQueueStateService,
    AgentSyncNotificationQueue syncNotificationQueue,
    IServiceScopeFactory scopeFactory,
    ILogger<AgentHub> logger) : Hub
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
        agentQueueStateService.Clear();
        // Clear cached workspace root when agent disconnects.
        // WorkspaceService is scoped, so resolve it from a fresh scope.
        await using var scope = scopeFactory.CreateAsyncScope();
        var workspaceService = scope.ServiceProvider.GetRequiredService<WorkspaceService>();
        workspaceService.ClearCachedRootPath();
        await base.OnDisconnectedAsync(exception);
    }

    /// <summary>Invoked by the agent for streaming command / stdout / stderr while a request is pending.</summary>
    public Task CommandOutput(string requestId, string? streamLabel, int kind, string text)
    {
        if (!Enum.IsDefined(typeof(AgentCommandStreamKind), kind))
            return Task.CompletedTask;

        try
        {
            AgentResponseDelivery.ReportStreamLine(
                requestId,
                new AgentCommandStreamLine(streamLabel, (AgentCommandStreamKind)kind, text));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "CommandOutput failed for request {RequestId}", requestId);
            throw;
        }

        return Task.CompletedTask;
    }

    /// <summary>Invoked by the agent when it completes a command. Delivers the response to the waiting caller.</summary>
    public Task ResponseCommand(string requestId, AgentCommandResponse response)
    {
        try
        {
            AgentResponseDelivery.Complete(requestId, response);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "ResponseCommand failed while completing request {RequestId}", requestId);
            throw;
        }

        return Task.CompletedTask;
    }

    /// <summary>Invoked by the agent when a hook fires: agent ran GitVersion (and fetch/commit counts) and pushes result for app to persist.</summary>
    public Task SyncCommand(RepositorySyncNotification notification)
    {
        syncNotificationQueue.Enqueue(notification);
        return Task.CompletedTask;
    }

    /// <summary>Invoked by the agent when it connects to report its SemVer version.</summary>
    public Task ReportSemVer(string semVer)
    {
        connectionTracker.ReportAgentSemVer(Context.ConnectionId, semVer);
        return Task.CompletedTask;
    }

    /// <summary>Invoked by the agent when its job queue status changes (total pending, per-workspace counts). JSON keys are strings.</summary>
    public Task ReportQueueStatus(int total, IReadOnlyDictionary<string, int>? byWorkspace)
    {
        IReadOnlyDictionary<int, int>? byWorkspaceInt = null;
        if (byWorkspace != null && byWorkspace.Count > 0)
        {
            var dict = new Dictionary<int, int>();
            foreach (var kv in byWorkspace)
                if (int.TryParse(kv.Key, out var wid) && kv.Value > 0)
                    dict[wid] = kv.Value;
            byWorkspaceInt = dict;
        }
        agentQueueStateService.ReportQueueStatus(total, byWorkspaceInt);
        return Task.CompletedTask;
    }
}
