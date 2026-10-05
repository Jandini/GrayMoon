using GrayMoon.Abstractions.Worker;
using GrayMoon.Abstractions.Notifications;
using GrayMoon.App.Services;
using GrayMoon.App.Services.GitChanges;
using GrayMoon.App.Services.Security;
using GrayMoon.Common.Git;
using Microsoft.AspNetCore.SignalR;

namespace GrayMoon.App.Hubs;

public sealed class WorkerHub(
    WorkerConnectionTracker connectionTracker,
    WorkerQueueStateService workerQueueStateService,
    WorkerSyncNotificationQueue syncNotificationQueue,
    WorkspaceGitChangesWriteQueue gitChangesWriteQueue,
    WorkerSecretService workerSecret,
    IServiceScopeFactory scopeFactory,
    ILogger<WorkerHub> logger) : Hub
{
    public override async Task OnConnectedAsync()
    {
        connectionTracker.OnWorkerConnected(Context.ConnectionId);
        // Refresh workspace root from settings when worker connects.
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
        connectionTracker.OnWorkerDisconnected(Context.ConnectionId);
        workerSecret.NoteWorkerDisconnected();
        workerQueueStateService.Clear();
        // Fail any request still in flight immediately rather than letting it wait out its full
        // WorkerBridge command timeout - there is only ever one worker connection, so no per-connection
        // filtering is needed here.
        WorkerResponseDelivery.FailAll("Worker disconnected before responding.");
        // Clear cached workspace root when worker disconnects.
        // WorkspaceService is scoped, so resolve it from a fresh scope.
        await using var scope = scopeFactory.CreateAsyncScope();
        var workspaceService = scope.ServiceProvider.GetRequiredService<WorkspaceService>();
        workspaceService.ClearCachedRootPath();
        await base.OnDisconnectedAsync(exception);
    }

    /// <summary>Invoked by the worker for streaming command / stdout / stderr while a request is pending.</summary>
    public Task CommandOutput(string requestId, string? streamLabel, int kind, string text)
    {
        if (!Enum.IsDefined(typeof(WorkerCommandStreamKind), kind))
            return Task.CompletedTask;

        try
        {
            WorkerResponseDelivery.ReportStreamLine(
                requestId,
                new WorkerCommandStreamLine(streamLabel, (WorkerCommandStreamKind)kind, text));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "CommandOutput failed for request {RequestId}", requestId);
            throw;
        }

        return Task.CompletedTask;
    }

    /// <summary>Invoked by the worker when it completes a command. Delivers the response to the waiting caller.</summary>
    public Task ResponseCommand(string requestId, WorkerCommandResponse response)
    {
        try
        {
            WorkerResponseDelivery.Complete(requestId, response);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "ResponseCommand failed while completing request {RequestId}", requestId);
            throw;
        }

        return Task.CompletedTask;
    }

    /// <summary>Invoked by the worker when a hook fires: worker ran GitVersion (and fetch/commit counts) and pushes result for app to persist.</summary>
    public Task SyncCommand(RepositorySyncNotification notification)
    {
        syncNotificationQueue.Enqueue(notification);
        return Task.CompletedTask;
    }

    /// <summary>Invoked by the worker when it connects to report its SemVer version.</summary>
    public Task ReportSemVer(string semVer)
    {
        connectionTracker.ReportWorkerSemVer(Context.ConnectionId, semVer);
        return Task.CompletedTask;
    }

    /// <summary>Invoked by the worker with an unsolicited Git Changes status snapshot for one repository
    /// (watcher-driven or post-mutation refresh). Enqueues immediately; persistence and broadcast happen
    /// on <see cref="WorkspaceGitChangesWriteQueue"/>'s background worker.</summary>
    public Task GitChangesSnapshotUpdated(GitChangesSnapshotNotification notification)
    {
        gitChangesWriteQueue.Enqueue(notification);
        return Task.CompletedTask;
    }

    /// <summary>Invoked by the worker when its job queue status changes (total pending, per-workspace counts). JSON keys are strings.</summary>
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
        workerQueueStateService.ReportQueueStatus(total, byWorkspaceInt);
        return Task.CompletedTask;
    }
}
