namespace GrayMoon.App.Services.Jobs;

/// <summary>
/// Process-wide lock: one mutating operation per workspace. Circuit
/// <see cref="IBackgroundJobService"/> attaches overlay handles to the running operation.
/// </summary>
public interface IWorkspaceOperationRunner
{
    /// <summary>Fires when any workspace operation starts, progresses, or ends. Raised on a background thread.</summary>
    event Action? Changed;

    WorkspaceOperation? GetRunning(int workspaceId);

    bool IsBusy(int workspaceId);

    /// <summary>
    /// Starts work when the workspace is idle. If a mutation is already running, returns
    /// that operation and does not invoke <paramref name="work"/>.
    /// <paramref name="overlayKey"/> is the originating page path; only that path shows the overlay.
    /// </summary>
    /// <returns>True when this call started new work; false when it returned the existing run.</returns>
    bool TryStart(
        int workspaceId,
        string operationKind,
        string overlayKey,
        string displayMessage,
        Func<WorkspaceOperation, CancellationToken, Task> work,
        out WorkspaceOperation operation);
}
