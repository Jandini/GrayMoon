using System.Collections.Concurrent;

namespace GrayMoon.Agent.Services.GitChanges;

/// <summary>
/// Remembers which workspace/repository database IDs a repository path belongs to, so a watcher-driven
/// refresh (which only knows a path) can attribute its push notification correctly. Populated whenever
/// the App asks about a repository (e.g. <c>GetGitChangeStatus</c>); a path the App has never asked
/// about simply has no lease and no registry entry, so watcher events for it are silently dropped.
/// </summary>
public sealed class GitChangesRepositoryRegistry
{
    private readonly ConcurrentDictionary<string, (int WorkspaceId, int RepositoryId)> _entries = new(StringComparer.OrdinalIgnoreCase);

    public void Register(string repoPath, int workspaceId, int repositoryId) =>
        _entries[GitChangesSnapshotCache.NormalizeKey(repoPath)] = (workspaceId, repositoryId);

    public bool TryGet(string repoPath, out int workspaceId, out int repositoryId)
    {
        if (_entries.TryGetValue(GitChangesSnapshotCache.NormalizeKey(repoPath), out var entry))
        {
            workspaceId = entry.WorkspaceId;
            repositoryId = entry.RepositoryId;
            return true;
        }

        workspaceId = 0;
        repositoryId = 0;
        return false;
    }
}
