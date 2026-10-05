using System.Text.Json.Serialization;

namespace GrayMoon.App.Models.Api;

/// <summary>Worker SearchFiles response.</summary>
public sealed class WorkerSearchFilesResponse
{
    [JsonPropertyName("files")]
    public List<WorkerSearchFileItemDto>? Files { get; set; }
}

public sealed class WorkerSearchFileItemDto
{
    [JsonPropertyName("repositoryName")]
    public string? RepositoryName { get; set; }

    [JsonPropertyName("filePath")]
    public string? FilePath { get; set; }

    [JsonPropertyName("fileName")]
    public string? FileName { get; set; }
}
