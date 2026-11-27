using System.Text.Json.Serialization;

namespace GrayMoon.Common.Manifest;

/// <summary>
/// Generated JSON for <c>graymoon.json</c>. The Worker is published as Native AOT, where JSON by reflection does not
/// exist. One set of options serves both directions: written indented in camelCase, read without regard to case.
/// </summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(WorkspaceManifest))]
internal sealed partial class WorkspaceManifestJsonContext : JsonSerializerContext;
