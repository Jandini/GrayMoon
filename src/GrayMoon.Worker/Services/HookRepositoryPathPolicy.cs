namespace GrayMoon.Worker.Services;

/// <summary>
/// Decides whether the <c>repositoryPath</c> of a git hook payload may be scanned. The hook listener is bound to
/// loopback, but any local process or a browser page can post to it, so the path is not trusted: it must be an
/// existing working tree that GrayMoon manages.
/// </summary>
/// <remarks>
/// A path is accepted when it is an existing directory with <c>.git</c> (a directory, or the pointer file of a
/// Feature worktree) and one of these holds:
/// <list type="bullet">
/// <item>it lies under a workspace root the App has named in a command (<see cref="KnownWorkspaceRoots"/>);</item>
/// <item>its repository carries a sync hook this Worker installed for the same workspace and repository ids. This
/// covers the time right after a Worker restart, before the App has sent a command, and a Feature worktree that
/// is still being created.</item>
/// </list>
/// A network path is only accepted under a known root, and is never probed otherwise: opening an attacker-chosen
/// UNC path would send the Worker account's credentials to that host.
/// </remarks>
public sealed class HookRepositoryPathPolicy(KnownWorkspaceRoots knownRoots)
{
    // The marker GitService writes at the top of every hook it installs. Persisted in users' hook files; the text
    // must stay equal to GitService.GrayMoonHookMarker (HookRepositoryPathPolicyTests installs real hooks to prove it).
    private const string GrayMoonHookMarker = "# Created by GrayMoon.Agent";

    private const int MaxPointerFileBytes = 4 * 1024;
    private const int MaxHookFileBytes = 64 * 1024;

    private static readonly string[] SyncHookNames =
        ["post-commit", "post-checkout", "post-merge", "pre-push", "reference-transaction"];

    /// <summary>Null when the path may be scanned; otherwise why it was refused (for the log, never sent to the caller).</summary>
    public string? GetRejectionReason(string? repositoryPath, int workspaceId, int repositoryId)
    {
        if (string.IsNullOrWhiteSpace(repositoryPath) || repositoryPath.Contains('\0') || !Path.IsPathFullyQualified(repositoryPath))
            return "the path is not an absolute path";

        string fullPath;
        try
        {
            fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoryPath));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return "the path is not a valid path";
        }

        var underKnownRoot = knownRoots.Contains(fullPath);
        if (!underKnownRoot && IsNetworkPath(fullPath))
            return "the path is a network path outside the known workspace roots";

        try
        {
            if (!Directory.Exists(fullPath))
                return "the path is not an existing directory";
            if (!WorkerRepositoryPaths.HasGitMetadata(fullPath))
                return "the path has no .git";
            if (underKnownRoot || CarriesSyncHook(fullPath, workspaceId, repositoryId))
                return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return $"the path could not be checked ({ex.GetType().Name})";
        }

        return "the path is outside the known workspace roots and its repository has no GrayMoon hook for these ids";
    }

    private static bool IsNetworkPath(string fullPath) =>
        fullPath.StartsWith(@"\\", StringComparison.Ordinal) || fullPath.StartsWith("//", StringComparison.Ordinal);

    private static bool CarriesSyncHook(string repositoryPath, int workspaceId, int repositoryId)
    {
        var commonDir = ResolveCommonGitDirectory(repositoryPath);
        if (commonDir is null)
            return false;

        // What the installed script contains: PAYLOAD="{\"repositoryId\":12,\"workspaceId\":3,...
        var ids = $"\\\"repositoryId\\\":{repositoryId},\\\"workspaceId\\\":{workspaceId},";
        var hooksDir = Path.Combine(commonDir, "hooks");
        foreach (var name in SyncHookNames)
        {
            var text = ReadSmallFile(Path.Combine(hooksDir, name), MaxHookFileBytes);
            if (text is not null
                && text.Contains(GrayMoonHookMarker, StringComparison.Ordinal)
                && text.Contains(ids, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The git directory that holds the shared hooks: <c>.git</c> itself, or for a linked worktree the common
    /// directory its <c>.git</c> pointer file leads to. Read from the files; git is not started for an unvetted path.
    /// </summary>
    private static string? ResolveCommonGitDirectory(string repositoryPath)
    {
        var dotGit = Path.Combine(repositoryPath, ".git");
        if (Directory.Exists(dotGit))
            return dotGit;

        var pointer = ReadSmallFile(dotGit, MaxPointerFileBytes);
        var firstLine = pointer?.Split('\n', 2)[0].Trim();
        if (firstLine is null || !firstLine.StartsWith("gitdir:", StringComparison.Ordinal))
            return null;

        var gitDir = ResolveLocal(repositoryPath, firstLine["gitdir:".Length..].Trim());
        if (gitDir is null)
            return null;

        var commonPointer = ReadSmallFile(Path.Combine(gitDir, "commondir"), MaxPointerFileBytes)?.Trim();
        return string.IsNullOrEmpty(commonPointer) ? gitDir : ResolveLocal(gitDir, commonPointer);
    }

    private static string? ResolveLocal(string baseDirectory, string path)
    {
        if (path.Length == 0 || path.Contains('\0'))
            return null;
        var full = Path.GetFullPath(Path.Combine(baseDirectory, path));
        return IsNetworkPath(full) ? null : full;
    }

    private static string? ReadSmallFile(string path, int maxBytes)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length > maxBytes)
            return null;
        return File.ReadAllText(path);
    }
}
