using System.Text.Json;
using System.Text.Json.Nodes;

namespace GrayMoon.App.Services.WorkspaceManifest;

/// <summary>
/// Maintains <c>codegraph.json</c> in the Workspace repository. The source repositories are git-ignored there (see
/// <see cref="ManagedGitIgnoreSection"/>) and CodeGraph skips git-ignored folders, so <c>include</c> lists each of them
/// (<c>Name/</c>). GrayMoon owns <c>include</c>; every other property is kept as it is. Output is 2-space indented
/// with LF line endings and a trailing newline.
/// </summary>
public static class CodeGraphConfigFile
{
    public const string FilePath = "codegraph.json";
    public const string IncludePropertyName = "include";

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    /// <summary>
    /// The new file content, or null when <paramref name="existingContent"/> is there but is not a JSON object
    /// (a hand-edited file is never overwritten just to fix it).
    /// </summary>
    public static string? Apply(string? existingContent, IEnumerable<string> sourceRepositoryNames)
    {
        ArgumentNullException.ThrowIfNull(sourceRepositoryNames);

        JsonObject root;
        if (string.IsNullOrWhiteSpace(existingContent))
            root = new JsonObject();
        else if (TryParseObject(existingContent) is { } parsed)
            root = parsed;
        else
            return null;

        var include = new JsonArray();
        foreach (var name in sourceRepositoryNames
                     .Where(n => !string.IsNullOrWhiteSpace(n))
                     .Select(n => n.Trim().TrimEnd('/', '\\'))
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
        {
            include.Add(name + "/");
        }

        // Replace in place, whatever its casing, so the property keeps its position in a hand-edited file.
        var existingKey = root.Select(p => p.Key)
            .FirstOrDefault(k => string.Equals(k, IncludePropertyName, StringComparison.OrdinalIgnoreCase));
        root[existingKey ?? IncludePropertyName] = include;

        var json = root.ToJsonString(WriteOptions).Replace("\r\n", "\n", StringComparison.Ordinal);
        return json + "\n";
    }

    private static JsonObject? TryParseObject(string content)
    {
        try
        {
            return JsonNode.Parse(content) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
