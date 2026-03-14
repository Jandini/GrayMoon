using System.Collections.Concurrent;

namespace GrayMoon.App.Services;

/// <summary>Holds agent job queue state (total + per-workspace) reported by the agent via SignalR. Clear when agent disconnects.</summary>
public sealed class AgentQueueStateService
{
    private readonly object _lock = new();
    private int _totalPending;
    private readonly ConcurrentDictionary<int, int> _byWorkspace = new();
    private event EventHandler? _queueStateChanged;

    /// <summary>Called by the agent when queue status changes. Invoked from hub (server thread).</summary>
    public void ReportQueueStatus(int total, IReadOnlyDictionary<int, int>? byWorkspace)
    {
        lock (_lock)
        {
            _totalPending = total;
            _byWorkspace.Clear();
            if (byWorkspace != null)
            {
                foreach (var kv in byWorkspace)
                {
                    if (kv.Value > 0)
                        _byWorkspace[kv.Key] = kv.Value;
                }
            }
        }

        _queueStateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Clear queue state when the agent disconnects.</summary>
    public void Clear()
    {
        lock (_lock)
        {
            _totalPending = 0;
            _byWorkspace.Clear();
        }

        _queueStateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void OnQueueStateChanged(EventHandler handler)
    {
        lock (_lock)
        {
            _queueStateChanged += handler;
        }
    }

    public void RemoveQueueStateChanged(EventHandler handler)
    {
        lock (_lock)
        {
            _queueStateChanged -= handler;
        }
    }

    public int GetTotalPendingCount()
    {
        lock (_lock)
            return _totalPending;
    }

    public int GetPendingCountForWorkspace(int workspaceId)
    {
        lock (_lock)
            return _byWorkspace.TryGetValue(workspaceId, out var count) ? count : 0;
    }

    /// <summary>True if the workspace has any jobs queued or in progress in the agent. For disabling actions or overlays.</summary>
    public bool HasWorkspaceJobsPending(int workspaceId) => GetPendingCountForWorkspace(workspaceId) > 0;
}
