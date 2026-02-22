using System.Text.Json.Serialization;

namespace GrayMoon.Agent.Models;

/// <summary>Single file match from workspace file search. FilePath is relative to repository root.</summary>
public sealed class WorkspaceFileSearchResult
{
    [JsonPropertyName("repositoryName")]
    public string RepositoryName { get; set; } = string.Empty;

    [JsonPropertyName("filePath")]
    public string FilePath { get; set; } = string.Empty;

    [JsonPropertyName("fileName")]
    public string FileName { get; set; } = string.Empty;
}
