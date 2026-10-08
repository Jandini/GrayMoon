using System.Text.Json;
using System.Text.Json.Nodes;
using GrayMoon.App.Components.Features;

namespace GrayMoon.App.Services.WorkspaceManifest;

/// <summary>
/// Recent Open-in tools stored on the Workspace repository's <c>.graymoon.json</c>.
/// The property is not part of the Workspace definition, so drift detection ignores it.
/// </summary>
internal static class WorkspaceManifestRecentTools
{
    public const string PropertyName = "recentOpenInTools";

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
    };

    public static IReadOnlyList<string> Read(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return [];

        try
        {
            using var doc = JsonDocument.Parse(content);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !TryGetProperty(doc.RootElement, PropertyName, out var property)
                || property.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var tools = new List<string>();
            foreach (var item in property.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String)
                    continue;
                var id = item.GetString();
                if (FeatureOpenInTools.IsRemembered(id) && !tools.Contains(id!))
                    tools.Add(id!);
            }

            return tools;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// Sets <c>recentOpenInTools</c> on an existing definition. Returns null when
    /// <paramref name="content"/> is not a JSON object, so a missing file is never created
    /// just to remember a tool.
    /// </summary>
    public static string? Apply(string? content, IReadOnlyList<string> tools)
    {
        if (string.IsNullOrWhiteSpace(content))
            return null;

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(content);
        }
        catch (JsonException)
        {
            return null;
        }

        if (node is not JsonObject obj)
            return null;

        RemoveProperty(obj, PropertyName);
        var kept = new List<string>();
        foreach (var tool in tools)
        {
            if (FeatureOpenInTools.IsRemembered(tool) && !kept.Contains(tool))
                kept.Add(tool);
        }

        if (kept.Count > 0)
        {
            var array = new JsonArray();
            foreach (var tool in kept)
                array.Add(tool);
            obj.Add(PropertyName, array);
        }

        var json = obj.ToJsonString(WriteOptions).Replace("\r\n", "\n", StringComparison.Ordinal);
        return json.EndsWith('\n') ? json : json + "\n";
    }

    /// <summary>Copies recent tools from an existing file onto a freshly serialized definition.</summary>
    public static string Preserve(string serialized, string? existingContent)
    {
        var recent = Read(existingContent);
        return recent.Count == 0 ? serialized : Apply(serialized, recent) ?? serialized;
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
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

    private static void RemoveProperty(JsonObject obj, string name)
    {
        string? match = null;
        foreach (var property in obj)
        {
            if (string.Equals(property.Key, name, StringComparison.OrdinalIgnoreCase))
            {
                match = property.Key;
                break;
            }
        }

        if (match is not null)
            obj.Remove(match);
    }
}
