using System.Text.Json.Serialization;

namespace GrayMoon.Agent.Jobs.Requests;

public sealed class ResolveGeneratedPackageReferencesRequest : WorkspaceCommandRequest
{
    [JsonPropertyName("workspaceName")] public string? WorkspaceName { get; set; }
    [JsonPropertyName("files")] public List<ResolveGeneratedPackageReferencesItem>? Files { get; set; }
}

public sealed class ResolveGeneratedPackageReferencesItem
{
    [JsonPropertyName("repositoryName")] public string? RepositoryName { get; set; }
    [JsonPropertyName("filePath")] public string? FilePath { get; set; }
    [JsonPropertyName("pattern")] public string? Pattern { get; set; }
}
