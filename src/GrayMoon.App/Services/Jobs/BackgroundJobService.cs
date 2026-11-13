using System.Collections.Concurrent;
using GrayMoon.App.Services.Ui;
using GrayMoon.Application.Features;

namespace GrayMoon.App.Services.Jobs;

public interface IBackgroundJobService
{
    /// <summary>Returns the most recent job for this key, or null if none.</summary>
    BackgroundJobHandle? GetJob(string jobKey);

    /// <summary>True when a job for this key is in Running state.</summary>
    bool IsRunning(string jobKey);

    /// <summary>
    /// True while any job of this circuit (browser tab) is running, on whatever page it was started. A full page
    /// reload ends the circuit and with it every such job, so a caller asks this before it forces one.
    /// </summary>
    bool HasRunningJobs { get; }

    /// <summary>
    /// Starts a new background job for jobKey. If a Running job already exists for the key,
    /// or the Workspace or context lock is held by another operation, the work is dropped and the returned handle
    /// shows the running job with <see cref="BackgroundJobHandle.WasStarted"/> false, and this tab is told with a
    /// warning toast (<see cref="BackgroundJobService.StartRefusedMessage"/>), so a caller need not report it again.
    /// Otherwise creates a new handle, stores it, and launches the work.
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
    private readonly ILogger<BackgroundJobService> _logger;
    private readonly IToastService _toast;

    /// <summary>The warning shown in this tab when a start is refused because another operation is running.</summary>
    internal const string StartRefusedMessage = "Another operation is still running, so this one was not started. Try again when it finishes.";

    public event Action? Changed;

    // Both this service and the toast service are scoped to the circuit, so the toast reaches the tab that asked.
    public BackgroundJobService(IWorkspaceOperationRunner runner, IToastService toast, ILogger<BackgroundJobService> logger)
    {
        _runner = runner;
        _toast = toast;
        _logger = logger;
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

    public bool HasRunningJobs => _jobs.Values.Any(job => job.State == BackgroundJobState.Running);

    public BackgroundJobHandle StartJob(string jobKey, string displayMessage,
        Func<BackgroundJobHandle, CancellationToken, Task> work)
    {
        if (_jobs.TryGetValue(jobKey, out var existing) && existing.State == BackgroundJobState.Running)
        {
            _logger.LogInformation(
                "Background job {JobKey} was not started: '{Running}' is already running for this key.",
                jobKey, existing.DisplayMessage);
            _toast.ShowWarning(StartRefusedMessage);
            return existing.AsNotStarted();
        }

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
                _logger.LogError(ex, "Background job {JobKey} failed: {Message}", jobKey, ex.Message);
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
        // Context-scoped overlay keys (/workspaces/{id}/ctx/{contextId}/...) use hierarchical context locks
        // so Feature A and Feature B mutations can run concurrently.
        if (_runner is IWorkspaceOperationLock hierarchical
            && WorkspaceJobKeys.TryGetContextId(jobKey, out _, out var contextIdValue))
        {
            var contextId = new WorkspaceFeatureContextId(contextIdValue);
            var contextStarted = hierarchical.TryStartContext(
                contextId,
                workspaceId,
                operationKind: jobKey,
                overlayKey: jobKey,
                displayMessage,
                async (operation, ct) =>
                {
                    var bound = Attach(jobKey, (WorkspaceOperation)operation);
                    await work(bound, ct);
                },
                out var locked);

            var concrete = (WorkspaceOperation)locked;
            return AttachStartResult(jobKey, concrete, contextStarted);
        }

        var started = _runner.TryStart(
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

        return AttachStartResult(jobKey, operation, started);
    }

    /// <summary>
    /// Attaches the overlay handle for <paramref name="operation"/>. When the lock was refused, the operation is the
    /// one already holding it and the caller's work was dropped: the returned handle shows that operation with
    /// <see cref="BackgroundJobHandle.WasStarted"/> false, so the caller cannot mistake it for its own run.
    /// </summary>
    private BackgroundJobHandle AttachStartResult(string jobKey, WorkspaceOperation operation, bool started)
    {
        var handle = WorkspaceJobKeys.OverlayMatches(jobKey, operation)
            ? Attach(jobKey, operation)
            : Attach(operation.OverlayKey, operation);
        if (started)
            return handle;

        _logger.LogInformation(
            "Background job {JobKey} was not started: '{Running}' ({RunningKey}) holds the lock.",
            jobKey, operation.DisplayMessage, operation.OverlayKey);
        _toast.ShowWarning(StartRefusedMessage);
        return handle.AsNotStarted();
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
