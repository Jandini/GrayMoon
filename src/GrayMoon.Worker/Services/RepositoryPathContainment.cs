namespace GrayMoon.Worker.Services;

/// <summary>
/// Checks that a path under a repository still lands inside it once symbolic links and junctions are followed.
/// <c>GitRepositoryPathValidator</c> compares text only, so <c>link/file.txt</c> passes it even when <c>link</c> is a
/// junction to another folder. Used before the Worker writes or deletes a file at a path the App sent. A link whose
/// target stays inside the repository is accepted, and so is a repository that itself lives behind a link.
/// </summary>
public static class RepositoryPathContainment
{
    private const int MaxLinkHops = 40;

    /// <summary>
    /// Null when <paramref name="fullPath"/> resolves inside <paramref name="repositoryRoot"/>; otherwise why it is
    /// refused. A path that does not exist yet is judged by the part of it that does.
    /// </summary>
    public static string? GetEscapeReason(string repositoryRoot, string fullPath)
    {
        try
        {
            var hops = 0;
            var realRoot = ResolveLinks(Path.GetFullPath(repositoryRoot), ref hops);
            hops = 0;
            var realPath = ResolveLinks(Path.GetFullPath(fullPath), ref hops);
            if (realRoot == null || realPath == null)
                return "Path passes through too many links.";

            var rootWithSeparator = realRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var inside = string.Equals(realPath, realRoot, StringComparison.OrdinalIgnoreCase)
                || realPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase);
            return inside ? null : "Path leaves the repository through a link.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A link that cannot be read is not followed for a write.
            return $"Path could not be checked for links: {ex.Message}";
        }
    }

    /// <summary>The path with every link in it replaced by its target; null when the hop limit is reached.</summary>
    private static string? ResolveLinks(string fullPath, ref int hops)
    {
        var root = Path.GetPathRoot(fullPath) ?? "";
        var segments = fullPath[root.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        var current = root;
        for (var i = 0; i < segments.Length; i++)
        {
            var next = Path.Combine(current, segments[i]);
            var target = LinkTarget(next);
            if (target == null)
            {
                current = next;
                continue;
            }

            if (++hops > MaxLinkHops)
                return null;
            // The target can contain links itself, so resolve it together with the rest of the path.
            var rest = segments[(i + 1)..];
            var redirected = rest.Length == 0 ? target : Path.Combine([target, .. rest]);
            return ResolveLinks(Path.GetFullPath(redirected), ref hops);
        }

        return current;
    }

    /// <summary>Full path a link points to; null for anything that is not a link (or does not exist).</summary>
    private static string? LinkTarget(string path)
    {
        FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
        if (!info.Exists || (info.Attributes & FileAttributes.ReparsePoint) == 0)
            return null;
        // Null for a reparse point that is not a link (a cloud placeholder, for example).
        return info.ResolveLinkTarget(returnFinalTarget: false)?.FullName;
    }
}
