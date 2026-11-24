using System.Collections.Concurrent;

namespace GrayMoon.App.Services.Versions;

/// <summary>
/// The parent branch version at a branch point, as the Worker calculated it. That version belongs to a commit and
/// does not change, so it is calculated once per commit for the life of the process. Without this, a repository whose
/// packages cannot be verified (the decision is "unresolved", which stores no version) ran GitVersion on the Worker
/// again in every recompute.
/// </summary>
/// <remarks>
/// Singleton, in memory, never persisted: a restart calculates again. Only an answer with a version for a commit
/// that is on the remote parent branch is kept; a failure, a missing version and "not on the remote parent" are
/// asked again. Bounded by <see cref="MaxEntries"/>; when full it is cleared, which only costs a recalculation.
/// </remarks>
public sealed class BranchPointVersionCache
{
    internal const int MaxEntries = 4096;

    private readonly ConcurrentDictionary<string, string> _versions = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The checkout (root, folder and repository), the parent branch and the commit identify one answer.</summary>
    public static string Key(string? workspaceRoot, string? workspaceFolderName, string repositoryName, string parentBranch, string commitSha) =>
        string.Join('|', workspaceRoot, workspaceFolderName, repositoryName, parentBranch.Trim(), commitSha.Trim());

    public bool TryGet(string key, out string version)
    {
        if (_versions.TryGetValue(key, out var found))
        {
            version = found;
            return true;
        }

        version = "";
        return false;
    }

    public void Set(string key, string version)
    {
        if (_versions.Count >= MaxEntries)
            _versions.Clear();
        _versions[key] = version.Trim();
    }
}
