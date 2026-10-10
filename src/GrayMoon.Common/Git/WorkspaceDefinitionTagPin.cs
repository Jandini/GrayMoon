using System.Text.Json;

namespace GrayMoon.Common.Git;

/// <summary>
/// A repository entry in <c>.graymoon.json</c> that is checked out on a tag stores the tag name and the
/// full commit hash that tag pointed at. Restore checks out that commit.
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

    /// <summary>
    /// The commit to check out for <paramref name="repositoryUrl"/>, or null when that repository is not pinned.
    /// <paramref name="definitionJson"/> null means the definition file is absent (nothing to check out).
    /// An unreadable file or an invalid pin is an error: the caller does not check out another revision.
    /// </summary>
    public static (string? Commit, string? Error) ReadCommit(string? definitionJson, string? repositoryUrl)
    {
        if (definitionJson is null)
            return (null, null);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(definitionJson);
        }
        catch (JsonException ex)
        {
            return (null, $"Workspace definition is not valid JSON: {ex.Message}");
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return (null, "Workspace definition is not valid JSON: the root element must be an object.");

            if (!TryProperty(document.RootElement, "repositories", out var repositories)
                || repositories.ValueKind == JsonValueKind.Null)
                return (null, null);

            if (repositories.ValueKind != JsonValueKind.Array)
                return (null, "Workspace definition repositories must be an array.");

            foreach (var repository in repositories.EnumerateArray())
            {
                if (repository.ValueKind != JsonValueKind.Object)
                    return (null, "Workspace definition repositories must be objects.");

                if (!TryProperty(repository, "repositoryUrl", out var urlElement)
                    || urlElement.ValueKind != JsonValueKind.String
                    || !RepositoryUrlIdentity.RepositoryUrlsEqual(urlElement.GetString(), repositoryUrl))
                    continue;

                var name = TryProperty(repository, "name", out var nameElement) && nameElement.ValueKind == JsonValueKind.String
                    ? nameElement.GetString()
                    : null;

                if (!TryReadString(repository, "tag", name, out var tag, out var tagError))
                    return (null, tagError);
                if (!TryReadString(repository, "commit", name, out var commit, out var commitError))
                    return (null, commitError);
                if (!TryNormalize(tag, commit, name, out _, out var normalizedCommit, out var pinError))
                    return (null, pinError);

                return (normalizedCommit, null);
            }

            return (null, null);
        }
    }

    private static bool TryReadString(JsonElement repository, string propertyName, string? repositoryName, out string? value, out string? error)
    {
        value = null;
        error = null;
        if (!TryProperty(repository, propertyName, out var element) || element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return true;

        if (element.ValueKind != JsonValueKind.String)
        {
            var label = string.IsNullOrWhiteSpace(repositoryName) ? "A repository" : $"Repository '{repositoryName}'";
            error = $"{label} {propertyName} must be a string.";
            return false;
        }

        value = element.GetString();
        return true;
    }

    private static bool TryProperty(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }
}
