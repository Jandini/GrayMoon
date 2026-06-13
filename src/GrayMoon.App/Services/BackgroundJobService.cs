using System.Collections.Concurrent;

namespace GrayMoon.App.Services;

public interface IBackgroundJobService
{
    /// <summary>Returns the most recent job for this key, or null if none.</summary>
    BackgroundJobHandle? GetJob(string jobKey);

    /// <summary>True when a job for this key is in Running state.</summary>
    bool IsRunning(string jobKey);

    /// <summary>
    /// Starts a new background job for jobKey. If a Running job already exists for the key,
    /// returns it unchanged. Otherwise creates a new handle, stores it, and launches the work.
    /// </summary>
    BackgroundJobHandle StartJob(string jobKey, string displayMessage,
        Func<BackgroundJobHandle, CancellationToken, Task> work);

    /// <summary>Fires on any job state change (start, progress, complete, fault, abort). Raised on a background thread.</summary>
    event Action? Changed;
}

/// <summary>
/// Scoped per Blazor circuit. Tracks background jobs by URL-path key; jobs survive page navigation
/// within the same browser tab. Disposing the service (tab closed) cancels all running jobs.
/// </summary>
public sealed class BackgroundJobService : IBackgroundJobService, IDisposable
{
    private readonly ConcurrentDictionary<string, BackgroundJobHandle> _jobs = new(StringComparer.OrdinalIgnoreCase);

    public event Action? Changed;

    public BackgroundJobHandle? GetJob(string jobKey)
        => _jobs.TryGetValue(jobKey, out var h) ? h : null;

    public bool IsRunning(string jobKey)
        => _jobs.TryGetValue(jobKey, out var h) && h.State == BackgroundJobState.Running;

    public BackgroundJobHandle StartJob(string jobKey, string displayMessage,
        Func<BackgroundJobHandle, CancellationToken, Task> work)
    {
        if (_jobs.TryGetValue(jobKey, out var existing) && existing.State == BackgroundJobState.Running)
            return existing;

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

    private void RaiseChanged()
    {
        var handler = Changed;
        handler?.Invoke();
    }

    public void Dispose()
    {
        foreach (var handle in _jobs.Values)
            handle.Dispose();
        _jobs.Clear();
    }
}
