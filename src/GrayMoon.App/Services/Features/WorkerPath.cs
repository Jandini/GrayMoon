namespace GrayMoon.App.Services.Features;

/// <summary>
/// Joins and inspects paths that live on the Worker's host, not the App's own host (the App
/// may run on Linux in a Docker container while the Worker runs on Windows, or either side could
/// run on Linux/macOS). <see cref="System.IO.Path"/> is never used here because it reflects the
/// App's own OS, not the Worker's.
///
/// Separator style (Windows <c>\</c> vs POSIX <c>/</c>) is inferred purely from the shape of the
/// path text itself (a POSIX path starts with <c>/</c>; a Windows path starts with a drive letter
/// like <c>C:\</c>) - this is not a "detect Linux and special-case it" check. Any path the Worker
/// already handed back (a user profile directory, a configured storage root, a persisted worktree
/// path) carries its own correct shape, so no explicit "which OS is the Worker" signal is needed:
/// a POSIX-shaped Worker path flows through POSIX-shaped the whole way automatically.
/// </summary>
internal static class WorkerPath
{
    internal static bool IsPosix(string? path) => !string.IsNullOrEmpty(path) && path.StartsWith('/');

    /// <summary>Normalizes separators and drops a trailing separator, keeping whichever style the path already uses.</summary>
    internal static string Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return string.Empty;

        var trimmed = path.Trim();
        var sep = IsPosix(trimmed) ? '/' : '\\';
        var normalized = trimmed.Replace('/', sep).Replace('\\', sep);
        return normalized.Length > 1 ? normalized.TrimEnd(sep) : normalized;
    }

    /// <summary>
    /// Joins path segments using the first non-empty segment's own separator style. Internal
    /// separators in every segment are normalized to that style; empty segments are skipped.
    /// </summary>
    internal static string Combine(params string[] parts)
    {
        var nonEmpty = parts.Where(p => !string.IsNullOrWhiteSpace(p)).ToArray();
        if (nonEmpty.Length == 0)
            return string.Empty;

        var sep = IsPosix(nonEmpty[0]) ? '/' : '\\';

        var root = nonEmpty[0].Replace('/', sep).Replace('\\', sep);
        root = root.Length > 1 ? root.TrimEnd(sep) : root;

        var result = root;
        for (var i = 1; i < nonEmpty.Length; i++)
        {
            var segment = nonEmpty[i].Replace('/', sep).Replace('\\', sep).Trim(sep);
            if (segment.Length == 0)
                continue;
            result = result.Length == 0 ? segment : $"{result}{sep}{segment}";
        }

        return result;
    }

    /// <summary>Last path segment, in whichever separator style the path already uses.</summary>
    internal static string GetFileName(string path)
    {
        if (string.IsNullOrEmpty(path))
            return string.Empty;

        var sep = IsPosix(path) ? '/' : '\\';
        var normalized = path.Replace('/', sep).Replace('\\', sep).TrimEnd(sep);
        var last = normalized.LastIndexOf(sep);
        return last >= 0 ? normalized[(last + 1)..] : normalized;
    }

    /// <summary>Parent directory, in whichever separator style the path already uses; null if there is none.</summary>
    internal static string? GetDirectoryName(string path)
    {
        if (string.IsNullOrEmpty(path))
            return null;

        var sep = IsPosix(path) ? '/' : '\\';
        var normalized = path.Replace('/', sep).Replace('\\', sep).TrimEnd(sep);
        var last = normalized.LastIndexOf(sep);
        return last >= 0 ? normalized[..last] : null;
    }

    /// <summary>
    /// True for Windows <c>X:\.graymoon</c> / <c>X:\.graymoon\...</c> (Feature storage incorrectly
    /// rooted on a drive - a legacy bug). A POSIX-shaped path never matches this Windows-only pattern.
    /// </summary>
    internal static bool IsLegacyWindowsDriveRootGraymoonPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;
        var normalized = path.Replace('/', '\\').TrimEnd('\\');
        // "C:\.graymoon" is 12 chars; longer paths must continue with '\'.
        if (normalized.Length < 12)
            return false;
        if (!char.IsLetter(normalized[0]) || normalized[1] != ':' || normalized[2] != '\\')
            return false;
        if (!normalized.AsSpan(3).StartsWith(".graymoon", StringComparison.OrdinalIgnoreCase))
            return false;
        return normalized.Length == 12 || normalized[12] == '\\';
    }
}
