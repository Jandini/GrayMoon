using System.Text.Json;
using System.Text.Json.Serialization;

namespace GrayMoon.Common.Manifest;

/// <summary>
/// The Workspace definition file (<c>graymoon.json</c>) at the root of the Workspace repository. Read and written only
/// through <see cref="IWorkspaceManifestFile"/>.
/// </summary>
/// <param name="CodeGraph">
/// <c>codegraph</c>: the workspace-wide switch. True makes GrayMoon maintain <c>codegraph.json</c> and
/// <c>/.codegraph/</c> in the managed <c>.gitignore</c>, and Repair Feature starts an index. False turns that
/// off, including the New Feature checkbox. Null (absent) leaves that maintenance off, and the New Feature
/// dialog can still offer an index when the CodeGraph CLI is installed. Owned by the file, not the database:
/// GrayMoon keeps it on rewrite and drift detection ignores it.
/// </param>
/// <param name="VersionFiles">
/// <c>versionFiles</c>: the Workspace files that have a version configuration (Files page). Written from the database;
/// on Restore the file is the source of truth and its entries are merged into the database. Drift detection ignores it.
/// Null (absent) when there are none, so a Workspace without version files keeps its definition unchanged.
/// </param>
/// <param name="Ai">
/// <c>ai</c>: optional per-provider model choices for AI-generated commit messages and pull request text. Owned by the
/// file like <see cref="CodeGraph"/>: GrayMoon keeps it on rewrite, drift detection ignores it, and nothing local (the
/// pinned provider, credentials, executable paths) is ever written into it. Null (absent) means no overrides.
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
    IReadOnlyList<WorkspaceManifestVersionFile>? VersionFiles = null,
    [property: JsonPropertyName("ai")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    WorkspaceManifestAi? Ai = null);

/// <summary>
/// The <c>ai</c> section. <c>providers</c> lists provider ids (<c>claude</c>, <c>codex</c>, <c>cursor</c>) each with an
/// optional model; a provider that is not listed uses GrayMoon's fast default model for it. Entries are kept exactly as
/// written, including ids this GrayMoon does not know, so a rewrite never loses one; validation happens where the
/// configuration is used (<see cref="GrayMoon.Common.Ai.AiProviderConfiguration"/>).
/// </summary>
public sealed record WorkspaceManifestAi(IReadOnlyList<WorkspaceManifestAiProvider> Providers);

/// <summary>One <c>ai.providers</c> entry. <c>model</c> absent or empty means GrayMoon's default for the provider (see <see cref="GrayMoon.Common.Ai.AiProviderConfiguration.DefaultModel"/>).</summary>
public sealed record WorkspaceManifestAiProvider(
    string Provider,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Model = null);

/// <summary>
/// Reads a JSON boolean, or the strings <c>"true"</c> / <c>"false"</c>, so a hand-edited value still works. Anything
/// else is an error that names the property; no other value is read as true or false.
/// </summary>
public abstract class LenientBooleanJsonConverterBase(string propertyName) : JsonConverter<bool?>
{
    public override bool HandleNull => true;

    public override bool? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType switch
        {
            JsonTokenType.True => true,
            JsonTokenType.False => false,
            JsonTokenType.Null => null,
            JsonTokenType.String when bool.TryParse(reader.GetString()?.Trim(), out var value) => value,
            _ => throw new JsonException($"{propertyName} must be true or false."),
        };

    public override void Write(Utf8JsonWriter writer, bool? value, JsonSerializerOptions options)
    {
        if (value is null)
            writer.WriteNullValue();
        else
            writer.WriteBooleanValue(value.Value);
    }
}

/// <summary>The lenient boolean of <c>codegraph</c>.</summary>
public sealed class LenientBooleanJsonConverter() : LenientBooleanJsonConverterBase("codegraph");

/// <summary>The lenient boolean of <c>reference</c>, on the workspace object and on a repository entry.</summary>
public sealed class ReferenceBooleanJsonConverter() : LenientBooleanJsonConverterBase("reference");

/// <summary>
/// The <c>workspace</c> object. The Workspace name is the folder on this computer and is not stored here.
/// A leftover <c>name</c> in an older file is ignored and left out the next time the file is written.
/// <param name="Reference">
/// <c>reference</c>: whether a Source repository added later starts read-only. It says nothing about the repositories
/// already listed; each of those has its own <c>reference</c>. Null (absent) means false. Written only in a version-2
/// file (see <see cref="WorkspaceManifestFile.Serialize"/>).
/// </param>
public sealed record WorkspaceManifestWorkspace(
    WorkspaceManifestProfile Profile,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [property: JsonConverter(typeof(ReferenceBooleanJsonConverter))]
    bool? Reference = null);

public sealed record WorkspaceManifestProfile(string Type, string Versioning, string Ci);

public sealed record WorkspaceManifestConnector(string Type, string Url);

/// <summary>
/// One repository entry. <c>reference</c> is the read-only state of this Source repository; <c>tag</c> + <c>commit</c>
/// is a tag pin; <c>branch</c> + <c>commit</c> is the recorded revision of a read-only repository on a branch
/// (<see cref="GrayMoon.Common.Git.WorkspaceDefinitionRevisionPin"/>). The file lists them in the order name,
/// repositoryUrl, connectorUrl, reference, tag, branch, commit.
/// </summary>
public sealed record WorkspaceManifestRepository(
    string Name,
    string RepositoryUrl,
    string ConnectorUrl,
    [property: JsonPropertyOrder(2)]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Tag = null,
    [property: JsonPropertyOrder(4)]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Commit = null,
    [property: JsonPropertyOrder(1)]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [property: JsonConverter(typeof(ReferenceBooleanJsonConverter))]
    bool? Reference = null,
    [property: JsonPropertyOrder(3)]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Branch = null);

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
