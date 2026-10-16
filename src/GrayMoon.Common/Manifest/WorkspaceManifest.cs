using System.Text.Json;
using System.Text.Json.Serialization;

namespace GrayMoon.Common.Manifest;

/// <summary>
/// The Workspace definition file (<c>graymoon.json</c>) at the root of the Workspace repository. Read and written only
/// through <see cref="IWorkspaceManifestFile"/>.
/// </summary>
/// <param name="CodeGraph">
/// <c>codegraph</c>: true lets GrayMoon maintain <c>codegraph.json</c> in the Workspace repository and give each Feature
/// its own CodeGraph index. Owned by the file, not the database: GrayMoon keeps it on
/// rewrite and drift detection ignores it. Null (absent) means off.
/// </param>
/// <param name="VersionFiles">
/// <c>versionFiles</c>: the Workspace files that have a version configuration (Files page). Written from the database;
/// on Restore the file is the source of truth and its entries are merged into the database. Drift detection ignores it.
/// Null (absent) when there are none, so a Workspace without version files keeps its definition unchanged.
/// </param>
public sealed record WorkspaceManifest(
    [property: JsonPropertyName("version")] int SchemaVersion,
    WorkspaceManifestWorkspace Workspace,
    IReadOnlyList<WorkspaceManifestConnector> Connectors,
    IReadOnlyList<WorkspaceManifestRepository> Repositories,
    [property: JsonPropertyName("codegraph")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [property: JsonConverter(typeof(LenientBooleanJsonConverter))]
    bool? CodeGraph = null,
    [property: JsonPropertyName("versionFiles")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<WorkspaceManifestVersionFile>? VersionFiles = null);

/// <summary>Reads a JSON boolean, or the strings <c>"true"</c> / <c>"false"</c>, so a hand-edited value still works.</summary>
public sealed class LenientBooleanJsonConverter : JsonConverter<bool?>
{
    public override bool HandleNull => true;

    public override bool? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType switch
        {
            JsonTokenType.True => true,
            JsonTokenType.False => false,
            JsonTokenType.Null => null,
            JsonTokenType.String when bool.TryParse(reader.GetString()?.Trim(), out var value) => value,
            _ => throw new JsonException("codegraph must be true or false."),
        };

    public override void Write(Utf8JsonWriter writer, bool? value, JsonSerializerOptions options)
    {
        if (value is null)
            writer.WriteNullValue();
        else
            writer.WriteBooleanValue(value.Value);
    }
}

public sealed record WorkspaceManifestWorkspace(string Name, WorkspaceManifestProfile Profile);

public sealed record WorkspaceManifestProfile(string Type, string Versioning, string Ci);

public sealed record WorkspaceManifestConnector(string Type, string Url);

public sealed record WorkspaceManifestRepository(
    string Name,
    string RepositoryUrl,
    string ConnectorUrl,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Tag = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Commit = null);

/// <summary>
/// One configured version file. <c>repository</c> is the repository folder name (a Source repository or the Workspace
/// repository), <c>path</c> is relative to that repository with <c>/</c> separators, and <c>pattern</c> holds the
/// version pattern one line per element.
/// </summary>
public sealed record WorkspaceManifestVersionFile(string Repository, string Path, IReadOnlyList<string> Pattern)
{
    /// <summary>The pattern as the database stores it: lines joined with <c>\n</c>.</summary>
    public string PatternText() => string.Join("\n", Pattern);

    /// <summary>Splits stored pattern text into non-blank lines without leading whitespace (the form written to the file).</summary>
    public static IReadOnlyList<string> SplitPattern(string? pattern) =>
        (pattern ?? string.Empty)
            .Split('\n')
            .Select(l => l.TrimEnd('\r').TrimStart())
            .Where(l => l.Trim().Length > 0)
            .ToList();

    /// <summary>Repository-relative path with <c>/</c> separators and no leading or trailing separator.</summary>
    public static string NormalizePath(string? path) =>
        (path ?? string.Empty).Trim().Replace('\\', '/').Trim('/');
}
