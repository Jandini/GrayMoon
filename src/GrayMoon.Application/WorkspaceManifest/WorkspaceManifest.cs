using System.Text.Json.Serialization;

namespace GrayMoon.Application.WorkspaceManifest;

public sealed record WorkspaceManifest(
    [property: JsonPropertyName("version")] int SchemaVersion,
    WorkspaceManifestWorkspace Workspace,
    IReadOnlyList<WorkspaceManifestConnector> Connectors,
    IReadOnlyList<WorkspaceManifestRepository> Repositories);

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
