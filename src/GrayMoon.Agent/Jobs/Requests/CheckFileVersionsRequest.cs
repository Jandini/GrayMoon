using System.Text.Json.Serialization;

namespace GrayMoon.Agent.Jobs.Requests;

public sealed class CheckFileVersionsRequest : WorkspaceCommandRequest
{
    [JsonPropertyName("workspaceName")] public string? WorkspaceName { get; set; }
    [JsonPropertyName("files")] public List<CheckFileVersionsItem>? Files { get; set; }
}

public sealed class CheckFileVersionsItem
{
    [JsonPropertyName("repositoryName")] public string? RepositoryName { get; set; }
    [JsonPropertyName("filePath")] public string? FilePath { get; set; }
    [JsonPropertyName("pattern")] public string? Pattern { get; set; }
    [JsonPropertyName("expectedVersions")] public Dictionary<string, string>? ExpectedVersions { get; set; }
}
