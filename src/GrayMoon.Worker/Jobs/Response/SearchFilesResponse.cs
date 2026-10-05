using System.Text.Json.Serialization;
using GrayMoon.Worker.Models;

namespace GrayMoon.Worker.Jobs.Response;

public sealed class SearchFilesResponse
{
    [JsonPropertyName("files")]
    public WorkspaceFileSearchResult[] Files { get; set; } = [];
}
