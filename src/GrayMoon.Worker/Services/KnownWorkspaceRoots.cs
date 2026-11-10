namespace GrayMoon.Worker.Services;

/// <summary>
/// The workspace roots the App has named in its commands since this Worker started. The Worker has no workspace-root
/// configuration of its own, so this is the only list of folders it has been told to manage. The git hook listener
/// uses it to decide whether a path in a hook payload belongs to GrayMoon.
/// </summary>
public sealed class KnownWorkspaceRoots
{
    /// <summary>Upper bound; a Worker serves a handful of roots (the Workspace root and the Feature roots).</summary>
    internal const int MaxRoots = 256;

    /// <summary>The process-wide list, fed by <see cref="WorkerRepositoryPaths.GetWorkspacePath"/>.</summary>
    public static KnownWorkspaceRoots Shared { get; } = new();

    private readonly HashSet<string> _roots = new(PathComparer);
    private readonly object _gate = new();

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>Records <paramref name="root"/>. A blank, relative or unreadable path is ignored; so is a root beyond the bound.</summary>
    public void Add(string? root)
    {
        var normalized = Normalize(root);
        if (normalized is null)
            return;
        lock (_gate)
        {
            if (_roots.Count < MaxRoots)
                _roots.Add(normalized);
        }
    }

    /// <summary>True when <paramref name="path"/> is one of the roots or lies below one. Never touches the disk.</summary>
    public bool Contains(string? path)
    {
        var normalized = Normalize(path);
        if (normalized is null)
            return false;
        lock (_gate)
        {
            foreach (var root in _roots)
            {
                if (string.Equals(normalized, root, PathComparison)
                    || normalized.StartsWith(root + Path.DirectorySeparatorChar, PathComparison))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static string? Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            return null;
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }
}
