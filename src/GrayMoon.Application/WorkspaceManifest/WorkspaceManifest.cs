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

public sealed record WorkspaceManifestRepository(string Name, string RepositoryUrl, string ConnectorUrl);
