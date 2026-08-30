using System.Text.Json.Serialization;

namespace GrayMoon.App.Models.Api;

/// <summary>Agent SearchFiles response.</summary>
public sealed class AgentSearchFilesResponse
{
    [JsonPropertyName("files")]
    public List<AgentSearchFileItemDto>? Files { get; set; }
}

public sealed class AgentSearchFileItemDto
{
    [JsonPropertyName("repositoryName")]
    public string? RepositoryName { get; set; }

    [JsonPropertyName("filePath")]
    public string? FilePath { get; set; }

    [JsonPropertyName("fileName")]
    public string? FileName { get; set; }
}
