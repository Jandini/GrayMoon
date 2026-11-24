namespace GrayMoon.App.Services.Worker;

/// <summary>
/// Holds how many repository updates the App has received and not yet applied (total and per Workspace), so the UI
/// can show that work the same way it shows the Worker's queue (<see cref="WorkerQueueStateService"/>).
/// Reported by <see cref="WorkerSyncNotificationQueue"/>. Singleton: every subscriber removes its handler in Dispose.
/// </summary>
public sealed class AppBackgroundWorkStateService
{
    private readonly object _lock = new();
    private readonly Dictionary<int, int> _byWorkspace = [];
    private int _totalPending;
    private event EventHandler? _stateChanged;

    public void Increment(int workspaceId)
    {
        lock (_lock)
        {
            _byWorkspace[workspaceId] = _byWorkspace.GetValueOrDefault(workspaceId) + 1;
            _totalPending++;
        }

        RaiseChanged();
    }

    /// <summary>A count never goes below zero; a decrement without a matching increment changes nothing.</summary>
    public void Decrement(int workspaceId)
    {
        lock (_lock)
        {
            if (!_byWorkspace.TryGetValue(workspaceId, out var count))
                return;

            if (count <= 1)
                _byWorkspace.Remove(workspaceId);
            else
                _byWorkspace[workspaceId] = count - 1;
            _totalPending--;
        }

        RaiseChanged();
    }

    public int GetTotalPendingCount()
    {
        lock (_lock)
            return _totalPending;
    }

    public int GetPendingCountForWorkspace(int workspaceId)
    {
        lock (_lock)
            return _byWorkspace.GetValueOrDefault(workspaceId);
    }

    public void OnStateChanged(EventHandler handler)
    {
        lock (_lock)
            _stateChanged += handler;
    }

    public void RemoveStateChanged(EventHandler handler)
    {
        lock (_lock)
            _stateChanged -= handler;
    }

    /// <summary>Raised on the reporting thread, outside the lock. Handlers must not touch page state directly.</summary>
    private void RaiseChanged()
    {
        EventHandler? handlers;
        lock (_lock)
            handlers = _stateChanged;

        handlers?.Invoke(this, EventArgs.Empty);
    }
}
