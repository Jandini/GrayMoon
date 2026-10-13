using System.Text.Json;

namespace GrayMoon.Common.Git;

/// <summary>
/// The repositories listed in a Workspace definition (<c>graymoon.json</c>). Each one is checked out as an
/// immediate child folder of the Workspace repository's working tree, named after the repository.
/// </summary>
public static class WorkspaceDefinitionRepositories
{
    public const string FileName = "graymoon.json";

    /// <summary>
    /// Folder names of the repositories listed in <paramref name="definitionJson"/>. Lenient: a missing, unreadable
    /// or oddly shaped definition yields an empty list, never an error, because callers only use it to narrow work.
    /// </summary>
    public static IReadOnlyList<string> ReadNames(string? definitionJson)
    {
        if (string.IsNullOrWhiteSpace(definitionJson))
            return [];

        try
        {
            using var document = JsonDocument.Parse(definitionJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !TryProperty(document.RootElement, "repositories", out var repositories)
                || repositories.ValueKind != JsonValueKind.Array)
                return [];

            var names = new List<string>();
            foreach (var repository in repositories.EnumerateArray())
            {
                if (repository.ValueKind != JsonValueKind.Object
                    || !TryProperty(repository, "name", out var nameElement)
                    || nameElement.ValueKind != JsonValueKind.String)
                    continue;

                var name = nameElement.GetString()?.Trim();
                if (!string.IsNullOrEmpty(name) && IsSingleFolderName(name)
                    && !names.Contains(name, StringComparer.OrdinalIgnoreCase))
                    names.Add(name);
            }

            return names;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>Folder names of the repositories listed in the definition at the root of <paramref name="repositoryPath"/>; empty when there is none.</summary>
    public static IReadOnlyList<string> ReadNamesFromDirectory(string repositoryPath)
    {
        try
        {
            var path = Path.Combine(repositoryPath, FileName);
            return File.Exists(path) ? ReadNames(File.ReadAllText(path)) : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    // A name that is not one plain folder name (".", "..", or one with a separator on any platform) can never be a sibling folder.
    private static bool IsSingleFolderName(string name)
        => name is not "." and not ".."
           && name.IndexOfAny(['/', '\\']) < 0;

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
