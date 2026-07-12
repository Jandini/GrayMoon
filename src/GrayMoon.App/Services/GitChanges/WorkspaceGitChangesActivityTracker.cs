using GrayMoon.Common.Git;
using Microsoft.Extensions.Options;

namespace GrayMoon.App.Services.GitChanges;

/// <summary>
/// Tracks which workspaces currently have an open Git Changes page, so background monitoring only
/// sweeps repositories the user is actually looking at instead of every repository in the database.
/// Ref-counted per workspace like <c>GitRepositoryWatcherManager</c>'s Agent-side watcher lease, but with
/// time-based expiry on top: a workspace stays "active" for a grace period after its last viewer leaves,
/// so a crashed/undisposed circuit cannot pin a workspace active forever and silently regress back toward
/// sweeping everything.
/// </summary>
public interface IWorkspaceGitChangesActivityTracker
{
    IDisposable Subscribe(int workspaceId);
    bool IsActive(int workspaceId);
    IReadOnlyCollection<int> GetActiveWorkspaceIds();
}

public sealed class WorkspaceGitChangesActivityTracker(IOptions<GitChangesOptions> options) : IWorkspaceGitChangesActivityTracker
{
    private readonly object _gate = new();
    private readonly Dictionary<int, Entry> _entries = [];

    private TimeSpan GraceWindow => TimeSpan.FromMinutes(Math.Max(1, options.Value.WorkspaceActivityGraceMinutes));

    public IDisposable Subscribe(int workspaceId)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(workspaceId, out var entry))
            {
                entry = new Entry();
                _entries[workspaceId] = entry;
            }

            entry.RefCount++;
            entry.LastActiveUtc = DateTimeOffset.UtcNow;
        }

        return new Lease(this, workspaceId);
    }

    public bool IsActive(int workspaceId)
    {
        lock (_gate)
        {
            return IsActiveNoLock(workspaceId, DateTimeOffset.UtcNow);
        }
    }

    public IReadOnlyCollection<int> GetActiveWorkspaceIds()
    {
        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow;
            List<int>? expired = null;
            var active = new List<int>();

            foreach (var (workspaceId, entry) in _entries)
            {
                if (IsActiveNoLock(workspaceId, now))
                {
                    active.Add(workspaceId);
                }
                else if (entry.RefCount <= 0)
                {
                    (expired ??= []).Add(workspaceId);
                }
            }

            if (expired != null)
            {
                foreach (var workspaceId in expired)
                {
                    _entries.Remove(workspaceId);
                }
            }

            return active;
        }
    }

    private bool IsActiveNoLock(int workspaceId, DateTimeOffset now)
    {
        if (!_entries.TryGetValue(workspaceId, out var entry))
        {
            return false;
        }

        return entry.RefCount > 0 || now - entry.LastActiveUtc < GraceWindow;
    }

    private void Release(int workspaceId)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(workspaceId, out var entry) && entry.RefCount > 0)
            {
                entry.RefCount--;
                entry.LastActiveUtc = DateTimeOffset.UtcNow;
            }
        }
    }

    private sealed class Entry
    {
        public int RefCount;
        public DateTimeOffset LastActiveUtc;
    }

    private sealed class Lease(WorkspaceGitChangesActivityTracker owner, int workspaceId) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            owner.Release(workspaceId);
        }
    }
}
