using System.Collections.Concurrent;

namespace GrayMoon.App.Services.Jobs;

public interface IBackgroundJobService
{
    /// <summary>Returns the most recent job for this key, or null if none.</summary>
    BackgroundJobHandle? GetJob(string jobKey);

    /// <summary>True when a job for this key is in Running state.</summary>
    bool IsRunning(string jobKey);

    /// <summary>
    /// Starts a new background job for jobKey. If a Running job already exists for the key,
    /// returns it unchanged. Otherwise creates a new handle, stores it, and launches the work.
    /// Workspace mutation keys share one process-wide run per workspace.
    /// </summary>
    BackgroundJobHandle StartJob(string jobKey, string displayMessage,
        Func<BackgroundJobHandle, CancellationToken, Task> work);

    /// <summary>Fires on any job state change (start, progress, complete, fault, abort). Raised on a background thread.</summary>
    event Action? Changed;
}

/// <summary>
/// Scoped per Blazor circuit. Tracks overlay handles by URL-path key; jobs survive page navigation
/// within the same browser tab. Workspace mutations are hosted by
/// <see cref="IWorkspaceOperationRunner"/> so a second tab attaches instead of starting a second run.
/// Disposing the service (tab closed) cancels circuit-only jobs and detaches overlay handles
/// without aborting a process-wide workspace operation.
/// </summary>
public sealed class BackgroundJobService : IBackgroundJobService, IDisposable
{
    private readonly ConcurrentDictionary<string, BackgroundJobHandle> _jobs = new(StringComparer.OrdinalIgnoreCase);
    private readonly IWorkspaceOperationRunner _runner;

    public event Action? Changed;

    public BackgroundJobService(IWorkspaceOperationRunner runner)
    {
        _runner = runner;
        _runner.Changed += RaiseChanged;
    }

    public BackgroundJobHandle? GetJob(string jobKey)
    {
        if (_jobs.TryGetValue(jobKey, out var existing) && existing.State == BackgroundJobState.Running)
            return existing;

        if (TryGetMatchingOperation(jobKey, out var operation) && operation != null)
            return Attach(jobKey, operation);

        return _jobs.TryGetValue(jobKey, out existing) ? existing : null;
    }

    public bool IsRunning(string jobKey)
    {
        if (_jobs.TryGetValue(jobKey, out var handle) && handle.State == BackgroundJobState.Running)
            return true;

        return WorkspaceJobKeys.IsMutationKey(jobKey, out var workspaceId) && _runner.IsBusy(workspaceId);
    }

    public BackgroundJobHandle StartJob(string jobKey, string displayMessage,
        Func<BackgroundJobHandle, CancellationToken, Task> work)
    {
        if (_jobs.TryGetValue(jobKey, out var existing) && existing.State == BackgroundJobState.Running)
            return existing;

        if (WorkspaceJobKeys.IsMutationKey(jobKey, out var workspaceId))
            return StartWorkspaceJob(workspaceId, jobKey, displayMessage, work);

        var handle = new BackgroundJobHandle(jobKey, displayMessage);
        handle.Changed += RaiseChanged;
        _jobs[jobKey] = handle;

        _ = Task.Run(async () =>
        {
            using var _ = TerminalSinkContext.Use(handle.Terminal);
            try
            {
                await work(handle, handle.CancellationToken);
                handle.MarkCompleted();
            }
            catch (OperationCanceledException)
            {
                handle.MarkAborted();
            }
            catch (Exception ex)
            {
                handle.MarkFaulted(ex);
            }
            finally
            {
                RaiseChanged();
            }
        });

        RaiseChanged();
        return handle;
    }

    private BackgroundJobHandle StartWorkspaceJob(
        int workspaceId,
        string jobKey,
        string displayMessage,
        Func<BackgroundJobHandle, CancellationToken, Task> work)
    {
        _runner.TryStart(
            workspaceId,
            operationKind: jobKey,
            overlayKey: jobKey,
            displayMessage,
            async (operation, ct) =>
            {
                var bound = Attach(jobKey, operation);
                await work(bound, ct);
            },
            out var operation);

        if (WorkspaceJobKeys.OverlayMatches(jobKey, operation))
            return Attach(jobKey, operation);

        return Attach(operation.OverlayKey, operation);
    }

    private bool TryGetMatchingOperation(string jobKey, out WorkspaceOperation? operation)
    {
        operation = null;
        if (!WorkspaceJobKeys.TryGetWorkspaceId(jobKey, out var workspaceId))
            return false;

        var running = _runner.GetRunning(workspaceId);
        if (running == null || running.State != BackgroundJobState.Running)
            return false;

        if (!WorkspaceJobKeys.OverlayMatches(jobKey, running))
            return false;

        operation = running;
        return true;
    }

    private BackgroundJobHandle Attach(string jobKey, WorkspaceOperation operation)
    {
        if (_jobs.TryGetValue(jobKey, out var existing) && existing.State == BackgroundJobState.Running && existing.IsBoundTo(operation))
            return existing;

        var handle = new BackgroundJobHandle(jobKey, operation);
        handle.Changed += RaiseChanged;
        _jobs[jobKey] = handle;
        return handle;
    }

    private void RaiseChanged()
    {
        var handler = Changed;
        handler?.Invoke();
    }

    public void Dispose()
    {
        _runner.Changed -= RaiseChanged;
        foreach (var handle in _jobs.Values)
            handle.Dispose();
        _jobs.Clear();
    }
}
