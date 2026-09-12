using System.Collections.Concurrent;
using GrayMoon.Common.Git;

namespace GrayMoon.Agent.Services.GitChanges;

/// <summary>
/// Per-repository monotonic snapshot version counter and latest-snapshot cache for the Git Changes
/// feature. Registered as a singleton so every command handler and the watcher coordinator share one
/// version sequence per repository - the App rejects snapshot versions older than what it already has.
/// </summary>
public sealed class GitChangesSnapshotCache
{
    private readonly ConcurrentDictionary<string, long> _versions = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, GitChangeSnapshot> _latestSnapshots = new(StringComparer.OrdinalIgnoreCase);

    // Seeded from DateTime.UtcNow.Ticks rather than 1 so a restarted Agent process (which loses this
    // in-memory counter) still issues versions far above whatever the App last persisted, instead of
    // having every post-restart snapshot silently rejected as stale until the counter catches back up.
    public long NextVersion(string repoPath)
    {
        var candidate = DateTime.UtcNow.Ticks;
        return _versions.AddOrUpdate(NormalizeKey(repoPath), candidate, (_, current) => Math.Max(candidate, current + 1));
    }

    public void SetLatest(string repoPath, GitChangeSnapshot snapshot) =>
        _latestSnapshots[NormalizeKey(repoPath)] = snapshot;

    public GitChangeSnapshot? GetLatest(string repoPath) =>
        _latestSnapshots.TryGetValue(NormalizeKey(repoPath), out var snapshot) ? snapshot : null;

    /// <summary>
    /// Drops the version counter and cached snapshot for a repository that is no longer being watched
    /// (its <see cref="GitRepositoryWatcherManager"/> lease has expired), so this dictionary does not grow
    /// unbounded for the lifetime of the process as repositories/workspaces are added and removed.
    /// </summary>
    public void Remove(string repoPath)
    {
        var key = NormalizeKey(repoPath);
        _versions.TryRemove(key, out _);
        _latestSnapshots.TryRemove(key, out _);
    }

    internal static string NormalizeKey(string repoPath) =>
        Path.GetFullPath(repoPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
