namespace GrayMoon.App.Models;

/// <summary>
/// The one place project identity keys are derived. A physical project is identified by its normalized
/// repository-relative path; a generated package row by its normalized package id. Names are never identity.
/// </summary>
public static class ProjectIdentity
{
    /// <summary>
    /// Normalizes a repository-relative path: unifies separators, drops empty and <c>.</c> segments and resolves
    /// <c>..</c>. Returns null for an empty, rooted (leading separator or drive) or repository-escaping path.
    /// Case is preserved; use <see cref="PhysicalKey"/> for comparison.
    /// </summary>
    public static string? NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        var unified = path.Trim().Replace('\\', '/');
        if (unified.StartsWith('/') || (unified.Length >= 2 && unified[1] == ':' && char.IsLetter(unified[0])))
            return null;

        var segments = new List<string>();
        foreach (var segment in unified.Split('/'))
        {
            var s = segment.Trim();
            if (s.Length == 0 || s == ".")
                continue;
            if (s == "..")
            {
                if (segments.Count == 0)
                    return null;
                segments.RemoveAt(segments.Count - 1);
                continue;
            }

            segments.Add(s);
        }

        return segments.Count == 0 ? null : string.Join('/', segments);
    }

    /// <summary>Case-insensitive (Windows) comparison key for a physical project path, or null when the path is not valid.</summary>
    public static string? PhysicalKey(string? path) => NormalizePath(path)?.ToLowerInvariant();

    /// <summary>Comparison key for a generated package row, or null when there is no package id.</summary>
    public static string? GeneratedKey(string? packageId) =>
        string.IsNullOrWhiteSpace(packageId) ? null : packageId.Trim().ToLowerInvariant();
}
