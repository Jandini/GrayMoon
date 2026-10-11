using GrayMoon.Application.Features;
using Microsoft.Extensions.DependencyInjection;

namespace GrayMoon.App.Services.Features;

/// <summary>
/// Refreshes the special Workspace checkout. Independent of Feature removal: background removal
/// must not start it, wait for it, or run it when the Feature is gone.
/// </summary>
public interface IWorkspaceCheckoutRefresh
{
    /// <summary>
    /// Starts the refresh without waiting. If the Workspace is busy, retries in the background
    /// instead of blocking the caller.
    /// </summary>
    void Start(int workspaceId, WorkspaceFeatureContextId contextId);

    /// <summary>Runs the refresh to completion, waiting until the Workspace lock is free.</summary>
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

    public async Task RunAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        CancellationToken cancellationToken = default)
    {
        IWorkspaceLockedOperation operation;
        while (!TryStart(workspaceId, contextId, out operation))
            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);

        await operation.WhenCompleted.WaitAsync(cancellationToken);
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
