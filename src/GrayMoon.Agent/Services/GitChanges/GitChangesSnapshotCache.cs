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

    public long NextVersion(string repoPath) =>
        _versions.AddOrUpdate(NormalizeKey(repoPath), 1, (_, current) => current + 1);

    public void SetLatest(string repoPath, GitChangeSnapshot snapshot) =>
        _latestSnapshots[NormalizeKey(repoPath)] = snapshot;

    public GitChangeSnapshot? GetLatest(string repoPath) =>
        _latestSnapshots.TryGetValue(NormalizeKey(repoPath), out var snapshot) ? snapshot : null;

    internal static string NormalizeKey(string repoPath) =>
        Path.GetFullPath(repoPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
