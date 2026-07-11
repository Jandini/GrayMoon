namespace GrayMoon.Common.Git;

/// <summary>
/// Validates a repository-relative path from an untrusted remote caller before it is used in any
/// git invocation or filesystem access: rejects absolute paths and traversal, normalizes separators,
/// and confirms the resolved path stays inside the repository root.
/// </summary>
public static class GitRepositoryPathValidator
{
    public static GitPathValidationResult Validate(string repositoryRoot, string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return GitPathValidationResult.Invalid("Path is empty.");
        }

        var normalized = relativePath.Replace('\\', '/').Trim();

        if (normalized.StartsWith('/') || IsWindowsAbsolute(normalized))
        {
            return GitPathValidationResult.Invalid("Absolute paths are not allowed.");
        }

        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            return GitPathValidationResult.Invalid("Path is empty.");
        }

        foreach (var segment in segments)
        {
            if (segment == "." || segment == "..")
            {
                return GitPathValidationResult.Invalid("Path traversal is not allowed.");
            }
        }

        var normalizedRelativePath = string.Join('/', segments);
        var rootFull = Path.GetFullPath(repositoryRoot);
        var candidateFull = Path.GetFullPath(Path.Combine(rootFull, normalizedRelativePath));

        var rootWithSeparator = rootFull.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;

        var staysInsideRoot =
            string.Equals(candidateFull, rootFull, StringComparison.OrdinalIgnoreCase) ||
            candidateFull.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase);

        if (!staysInsideRoot)
        {
            return GitPathValidationResult.Invalid("Resolved path escapes the repository root.");
        }

        return GitPathValidationResult.Valid(candidateFull, normalizedRelativePath);
    }

    private static bool IsWindowsAbsolute(string normalized) =>
        normalized.Length >= 2 && normalized[1] == ':';
}

public sealed record GitPathValidationResult
{
    public required bool IsValid { get; init; }
    public string? ErrorMessage { get; init; }
    public string? FullPath { get; init; }
    public string? NormalizedRelativePath { get; init; }

    public static GitPathValidationResult Valid(string fullPath, string normalizedRelativePath) => new()
    {
        IsValid = true,
        FullPath = fullPath,
        NormalizedRelativePath = normalizedRelativePath,
    };

    public static GitPathValidationResult Invalid(string errorMessage) => new()
    {
        IsValid = false,
        ErrorMessage = errorMessage,
    };
}
