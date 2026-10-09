namespace GrayMoon.Worker.Services.GitChanges;

/// <summary>
/// Set of folders the Worker must not watch or scan right now because a delete is about to happen (or is happening)
/// there. A path is blocked when it equals, or lies beneath, a blocked folder. Scopes are reference counted so
/// concurrent removals under one Feature folder release independently.
/// </summary>
public sealed class RepositoryPathGate
{
    private readonly object _lock = new();
    private readonly List<string> _blocked = [];

    public IDisposable Block(IEnumerable<string> paths)
    {
        var normalized = paths
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(GitChangesSnapshotCache.NormalizeKey)
            .ToList();
        lock (_lock)
        {
            _blocked.AddRange(normalized);
        }

        return new Scope(this, normalized);
    }

    public bool IsBlocked(string path) => IsUnder(path, Snapshot());

    internal IReadOnlyList<string> Snapshot()
    {
        lock (_lock)
        {
            return [.. _blocked];
        }
    }

    internal static bool IsUnder(string path, IEnumerable<string> roots)
    {
        string key;
        try
        {
            key = GitChangesSnapshotCache.NormalizeKey(path);
        }
        catch (Exception)
        {
            return false;
        }

        foreach (var root in roots)
        {
            if (string.Equals(key, root, StringComparison.OrdinalIgnoreCase)
                || key.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private void Unblock(List<string> paths)
    {
        lock (_lock)
        {
            foreach (var path in paths)
            {
                _blocked.Remove(path);
            }
        }
    }

    private sealed class Scope(RepositoryPathGate owner, List<string> paths) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                owner.Unblock(paths);
            }
        }
    }
}
