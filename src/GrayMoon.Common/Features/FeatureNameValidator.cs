namespace GrayMoon.Common.Features;

/// <summary>
/// Validates a Feature name before it is used as a Git branch name, a Windows path segment and (in
/// error messages and logs) a shell argument. Stricter than plain Git where the three rule sets
/// disagree, never looser: every name this accepts also passes <c>git check-ref-format --branch</c>.
/// </summary>
public static class FeatureNameValidator
{
    private const int MaxLength = 100;

    private static readonly char[] GitForbiddenChars = ['~', '^', ':', '?', '*', '[', '\\'];
    private static readonly char[] WindowsForbiddenChars = ['<', '>', '"', '|'];
    private static readonly char[] ShellForbiddenChars = ['&', ';', '%', '!', '$', '`'];

    private static readonly HashSet<string> WindowsReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>Returns null when <paramref name="name"/> is valid, otherwise a short user-facing reason.</summary>
    public static string? Validate(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "Feature name cannot be empty.";

        if (name.Length > MaxLength)
            return $"Feature name cannot be longer than {MaxLength} characters.";

        if (name.Any(char.IsControl))
            return "Feature name cannot contain control characters.";

        if (name.Contains(' '))
            return "Feature name cannot contain spaces.";

        if (name == "@")
            return "Feature name cannot be '@'.";

        if (name.Contains("@{"))
            return "Feature name cannot contain '@{'.";

        if (name.Contains(".."))
            return "Feature name cannot contain '..'.";

        if (name.Contains("//"))
            return "Feature name cannot contain '//'.";

        if (name.StartsWith('-'))
            return "Feature name cannot start with '-'.";

        if (name.EndsWith('/') || name.EndsWith('.'))
            return "Feature name cannot end with '/' or '.'.";

        foreach (var c in GitForbiddenChars)
        {
            if (name.Contains(c))
                return $"Feature name cannot contain '{c}'.";
        }

        foreach (var c in WindowsForbiddenChars)
        {
            if (name.Contains(c))
                return $"Feature name cannot contain '{c}'.";
        }

        foreach (var c in ShellForbiddenChars)
        {
            if (name.Contains(c))
                return $"Feature name cannot contain '{c}'.";
        }

        var segments = name.Split('/');
        foreach (var segment in segments)
        {
            if (segment.Length == 0)
                return "Feature name cannot contain an empty path segment ('//' or a leading/trailing '/').";

            if (segment.StartsWith('.'))
                return "Feature name cannot have a path segment that starts with '.'.";

            if (segment.EndsWith(".lock", StringComparison.OrdinalIgnoreCase))
                return "Feature name cannot have a path segment that ends with '.lock'.";

            if (segment.EndsWith(' ') || segment.EndsWith('.'))
                return "Feature name cannot have a path segment that ends with a space or '.'.";

            var dotIndex = segment.IndexOf('.');
            var reservedCandidate = dotIndex >= 0 ? segment[..dotIndex] : segment;
            if (WindowsReservedNames.Contains(reservedCandidate))
                return $"Feature name cannot use the reserved Windows name '{reservedCandidate}'.";
        }

        return null;
    }
}
