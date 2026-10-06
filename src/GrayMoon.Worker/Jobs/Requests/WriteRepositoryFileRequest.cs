using System.Text.Json.Serialization;

namespace GrayMoon.Worker.Jobs.Requests;

public sealed class WriteRepositoryFileRequest : WorkspaceCommandRequest
{
    [JsonPropertyName("workspaceName")]
    public string? WorkspaceName { get; set; }

    [JsonPropertyName("repositoryName")]
    public string? RepositoryName { get; set; }

    /// <summary>Repository-relative path, validated by <c>GitRepositoryPathValidator</c>.</summary>
    [JsonPropertyName("filePath")]
    public string? FilePath { get; set; }

    /// <summary>Full file content, written as UTF-8 without a byte order mark.</summary>
    [JsonPropertyName("content")]
    public string? Content { get; set; }

    /// <summary>When true (default) the existing bytes are compared first and the write is skipped when equal.</summary>
    [JsonPropertyName("onlyIfChanged")]
    public bool OnlyIfChanged { get; set; } = true;
}
