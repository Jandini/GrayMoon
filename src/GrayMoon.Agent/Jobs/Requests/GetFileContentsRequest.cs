using System.Text.Json.Serialization;

namespace GrayMoon.Agent.Jobs.Requests;

public sealed class GetFileContentsRequest : WorkspaceCommandRequest
{
    [JsonPropertyName("workspaceName")] public string? WorkspaceName { get; set; }
    [JsonPropertyName("repositoryName")] public string? RepositoryName { get; set; }

    /// <summary>File path relative to the repository root.</summary>
    [JsonPropertyName("filePath")] public string? FilePath { get; set; }

    /// <summary>
    /// When true, return file bytes as Base64 in <c>contentBase64</c> (for images and other binary).
    /// When false (default), return UTF-8 text in <c>content</c>.
    /// </summary>
    [JsonPropertyName("asBase64")] public bool AsBase64 { get; set; }
}
