using System.Text.Json;
using System.Text.Json.Serialization;

namespace GrayMoon.Application.WorkspaceManifest;

/// <param name="CodeGraph">
/// <c>codegraph</c>: true lets GrayMoon maintain <c>codegraph.json</c> in the Workspace repository and give each Feature
/// its own CodeGraph index when the Workspace root has one. Owned by the file, not the database: GrayMoon keeps it on
/// rewrite and drift detection ignores it. Null (absent) means off.
/// </param>
public sealed record WorkspaceManifest(
    [property: JsonPropertyName("version")] int SchemaVersion,
    WorkspaceManifestWorkspace Workspace,
    IReadOnlyList<WorkspaceManifestConnector> Connectors,
    IReadOnlyList<WorkspaceManifestRepository> Repositories,
    [property: JsonPropertyName("codegraph")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [property: JsonConverter(typeof(LenientBooleanJsonConverter))]
    bool? CodeGraph = null);

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
/// One source repository's tag pin in the Workspace definition. Both <see cref="Tag"/> and <see cref="Commit"/>
/// null clears the pin. Setting a pin requires both.
/// </summary>
public sealed record WorkspaceRepositoryTagPinChange(string RepositoryUrl, string? Tag, string? Commit);
