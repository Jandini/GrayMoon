using System.Text.Json.Serialization;
using GrayMoon.Agent.Models;

namespace GrayMoon.Agent.Jobs.Response;

public sealed class SearchFilesResponse
{
    [JsonPropertyName("files")]
    public WorkspaceFileSearchResult[] Files { get; set; } = [];
}
