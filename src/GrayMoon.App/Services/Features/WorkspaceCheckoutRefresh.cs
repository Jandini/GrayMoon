using System.Collections.Concurrent;
using GrayMoon.Application.Features;
using Microsoft.Extensions.DependencyInjection;

namespace GrayMoon.App.Services.Features;

/// <summary>
/// Refreshes the special Workspace checkout. The Feature removal queue runs it to its end before it removes a
/// Feature, so the Workspace is current when the user creates the next Feature. It does not run beside the removal
/// it belongs to: a Sync of every Workspace repository and the removal of every worktree compete for git processes,
/// and on a machine where one costs over a second the removal took 160 to 225 s beside the Sync against 93 s alone
/// (logs of 2026-11-25).
/// </summary>
public interface IWorkspaceCheckoutRefresh
{
    /// <summary>
    /// Starts the refresh without waiting. If the Workspace is busy, retries in the background
    /// instead of blocking the caller.
    /// </summary>
    void Start(int workspaceId, WorkspaceFeatureContextId contextId);

    /// <summary>
    /// Runs the refresh to completion, waiting until the Workspace lock is free. A call that arrives while a
    /// refresh of the same Workspace started by this method is waiting or running joins that one.
    /// </summary>
    Task RunAsync(int workspaceId, WorkspaceFeatureContextId contextId, CancellationToken cancellationToken = default);
}

public sealed class WorkspaceCheckoutRefresh(
    IServiceScopeFactory scopeFactory,
    IWorkspaceOperationLock operationLock,
    ILogger<WorkspaceCheckoutRefresh> logger) : IWorkspaceCheckoutRefresh
{
    public void Start(int workspaceId, WorkspaceFeatureContextId contextId)
    {
        if (TryStart(workspaceId, contextId, out _))
            return;

        _ = WaitAndStartAsync(workspaceId, contextId);
    }

    // Workspace id -> the refresh RunAsync is waiting to start or running.
    private readonly ConcurrentDictionary<int, Lazy<Task>> _running = new();

    public Task RunAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        CancellationToken cancellationToken = default)
    {
        var run = _running.GetOrAdd(
            workspaceId,
            _ => new Lazy<Task>(() => RunSharedAsync(workspaceId, contextId, cancellationToken)));
        return run.Value.WaitAsync(cancellationToken);
    }

    private async Task RunSharedAsync(int workspaceId, WorkspaceFeatureContextId contextId, CancellationToken cancellationToken)
    {
        // Leave the caller before the first lock attempt, so the entry is in the dictionary before it can be removed.
        await Task.Yield();
        try
        {
            IWorkspaceLockedOperation operation;
            while (!TryStart(workspaceId, contextId, out operation))
                await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);

            await operation.WhenCompleted.WaitAsync(cancellationToken);
        }
        finally
        {
            _running.TryRemove(workspaceId, out _);
        }
    }

    private async Task WaitAndStartAsync(int workspaceId, WorkspaceFeatureContextId contextId)
    {
        try
        {
            while (!TryStart(workspaceId, contextId, out _))
                await Task.Delay(TimeSpan.FromMilliseconds(500));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Workspace status refresh could not start for workspace {WorkspaceId}.", workspaceId);
        }
    }

    private bool TryStart(int workspaceId, WorkspaceFeatureContextId contextId, out IWorkspaceLockedOperation operation)
        => operationLock.TryStartStructural(
            workspaceId,
            "refresh-workspace",
            WorkspaceJobKeys.RepositoriesOverlayKey(workspaceId),
            "Refreshing workspace state...",
            (op, ct) => RefreshAsync(op, workspaceId, contextId, ct),
            out operation);

    private async Task RefreshAsync(
        IWorkspaceLockedOperation operation,
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        CancellationToken cancellationToken)
    {
        operation.ReportProgress("Refreshing Workspace repository status...");

        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var git = scope.ServiceProvider.GetRequiredService<WorkspaceGitService>();
            await git.SyncAsync(
                workspaceId,
                contextId,
                repositoryIds: null,
                cancellationToken: cancellationToken);
            await git.RecomputeAndBroadcastWorkspaceSyncedAsync(workspaceId, contextId, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(
                ex,
                "Workspace status refresh failed for workspace {WorkspaceId}. A later Sync can recover.",
                workspaceId);
        }
    }
}
