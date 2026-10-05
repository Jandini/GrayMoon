namespace GrayMoon.App.Services.GitChanges;

/// <summary>
/// Tracks which Feature contexts the periodic Git Changes monitoring sweep must skip while Remove
/// Feature is deleting their worktrees, so the sweep never asks the Agent to scan a path that is
/// being deleted at the same time (D2). Reference-counted per context id, never per workspace: the
/// special Workspace context is never paused by any caller, so its monitoring keeps running while a
/// sibling Feature is removed.
/// </summary>
public interface IWorkspaceGitChangesMonitoringPause
{
    /// <summary>Pauses the sweep for this context id until the returned lease is disposed.</summary>
    IDisposable Pause(int contextId);

    bool IsPaused(int contextId);
}

public sealed class WorkspaceGitChangesMonitoringPause : IWorkspaceGitChangesMonitoringPause
{
    private readonly object _gate = new();
    private readonly Dictionary<int, int> _refCounts = [];

    public IDisposable Pause(int contextId)
    {
        lock (_gate)
        {
            _refCounts[contextId] = _refCounts.GetValueOrDefault(contextId) + 1;
        }

        return new Lease(this, contextId);
    }

    public bool IsPaused(int contextId)
    {
        lock (_gate)
        {
            return _refCounts.TryGetValue(contextId, out var count) && count > 0;
        }
    }

    private void Release(int contextId)
    {
        lock (_gate)
        {
            if (!_refCounts.TryGetValue(contextId, out var count))
                return;

            if (count <= 1)
                _refCounts.Remove(contextId);
            else
                _refCounts[contextId] = count - 1;
        }
    }

    private sealed class Lease(WorkspaceGitChangesMonitoringPause owner, int contextId) : IDisposable
    {
        private bool _released;

        public void Dispose()
        {
            if (_released)
                return;

            _released = true;
            owner.Release(contextId);
        }
    }
}
