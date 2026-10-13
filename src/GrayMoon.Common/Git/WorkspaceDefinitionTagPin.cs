namespace GrayMoon.Common.Git;

/// <summary>
/// A repository entry in <c>graymoon.json</c> that is checked out on a tag stores the tag name and the
/// full commit hash that tag pointed at. Restore checks out that commit. These are the value rules for a pin;
/// reading them from the file is <see cref="Manifest.IWorkspaceManifestFile.ReadTagPinCommit"/>.
/// </summary>
public static class WorkspaceDefinitionTagPin
{
    public static bool IsFullCommitHash(string? commit)
    {
        if (string.IsNullOrWhiteSpace(commit))
            return false;

        var value = commit.Trim();
        if (value.Length is not 40 and not 64)
            return false;

        foreach (var character in value)
        {
            if (!Uri.IsHexDigit(character))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Both values empty means the repository is not on a tag. A tag pin is a tag name plus a full commit
    /// hash; either one alone is invalid.
    /// </summary>
    public static bool TryNormalize(
        string? tag,
        string? commit,
        string? repositoryName,
        out string? normalizedTag,
        out string? normalizedCommit,
        out string? error)
    {
        normalizedTag = string.IsNullOrWhiteSpace(tag) ? null : tag.Trim();
        normalizedCommit = string.IsNullOrWhiteSpace(commit) ? null : commit.Trim().ToLowerInvariant();
        error = null;

        if (normalizedTag is null && normalizedCommit is null)
            return true;

        var label = string.IsNullOrWhiteSpace(repositoryName)
            ? "A repository"
            : $"Repository '{repositoryName.Trim()}'";

        if (normalizedTag is null || normalizedCommit is null)
        {
            error = $"{label} is on a tag but is missing the tag name or the full commit hash.";
            return false;
        }

        if (!IsFullCommitHash(normalizedCommit))
        {
            error = $"{label} commit must be the full commit hash.";
            return false;
        }

        return true;
    }
}
