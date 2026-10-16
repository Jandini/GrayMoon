using System.Text.RegularExpressions;

namespace GrayMoon.Common.Ai;

/// <summary>A dependency change read from a project file patch: a package or project reference added, removed or re-versioned.</summary>
public sealed record ProjectReferenceChange(string Kind, string Name, string? OldVersion, string? NewVersion)
{
    public string Describe() => (OldVersion, NewVersion) switch
    {
        _ when Kind == "ProjectReference" => $"project reference {Name} {(NewVersion is null ? "removed" : "added")}",
        (null, { } added) => $"{Name} {added} added",
        ({ } removed, null) => $"{Name} {removed} removed",
        ({ } from, { } to) => $"{Name} {from} -> {to}",
        _ => Name,
    };
}

/// <summary>
/// Structured facts from <c>.csproj</c>/<c>.props</c>/<c>.targets</c> patches: <c>PackageReference</c>,
/// <c>PackageVersion</c> and <c>ProjectReference</c> lines that were added or removed. A reference whose version did not
/// change (re-formatting, moved line) is not a fact. Only single-line elements are recognized; anything else stays in the
/// excerpt for the model to read.
/// </summary>
public static partial class ProjectFileFacts
{
    [GeneratedRegex(
        @"^(?<sign>[+-])\s*<(?<kind>PackageReference|PackageVersion|ProjectReference)\s+(?:Include|Update)\s*=\s*""(?<name>[^""]+)""(?:[^>]*?\sVersion\s*=\s*""(?<version>[^""]*)"")?",
        RegexOptions.CultureInvariant)]
    private static partial Regex ReferenceLine();

    public static IReadOnlyList<ProjectReferenceChange> Extract(string? patch)
    {
        if (string.IsNullOrEmpty(patch))
            return [];

        var removed = new Dictionary<(string Kind, string Name), string?>();
        var added = new Dictionary<(string Kind, string Name), string?>();
        foreach (var line in patch.Split('\n'))
        {
            var match = ReferenceLine().Match(line.TrimEnd('\r'));
            if (!match.Success)
                continue;

            var key = (match.Groups["kind"].Value, match.Groups["name"].Value);
            var version = match.Groups["version"].Success && match.Groups["version"].Value.Length > 0
                ? match.Groups["version"].Value
                : null;
            if (match.Groups["sign"].Value == "+")
                added[key] = version;
            else
                removed[key] = version;
        }

        var changes = new List<ProjectReferenceChange>();
        foreach (var key in removed.Keys.Union(added.Keys).OrderBy(k => k.Kind, StringComparer.Ordinal).ThenBy(k => k.Name, StringComparer.OrdinalIgnoreCase))
        {
            var wasRemoved = removed.TryGetValue(key, out var oldVersion);
            var wasAdded = added.TryGetValue(key, out var newVersion);
            if (wasRemoved && wasAdded && string.Equals(oldVersion, newVersion, StringComparison.Ordinal))
                continue;

            if (key.Kind == "ProjectReference")
            {
                if (wasRemoved && wasAdded)
                    continue;
                changes.Add(new ProjectReferenceChange(key.Kind, key.Name, wasRemoved ? "(reference)" : null, wasAdded ? "(reference)" : null));
                continue;
            }

            changes.Add(new ProjectReferenceChange(key.Kind, key.Name, wasRemoved ? oldVersion ?? "(no version)" : null, wasAdded ? newVersion ?? "(no version)" : null));
        }

        return changes;
    }
}
