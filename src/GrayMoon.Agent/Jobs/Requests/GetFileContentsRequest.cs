using System.Text.Json.Serialization;

namespace GrayMoon.Agent.Jobs.Requests;

public sealed class GetFileContentsRequest
{
    [JsonPropertyName("workspaceName")] public string? WorkspaceName { get; set; }
    [JsonPropertyName("repositoryName")] public string? RepositoryName { get; set; }

    /// <summary>File path relative to the repository root.</summary>
    [JsonPropertyName("filePath")] public string? FilePath { get; set; }
}
