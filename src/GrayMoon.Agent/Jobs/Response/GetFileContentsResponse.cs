using System.Text.Json.Serialization;

namespace GrayMoon.Agent.Jobs.Response;

public sealed class GetFileContentsResponse
{
    [JsonPropertyName("content")] public string? Content { get; set; }
    [JsonPropertyName("errorMessage")] public string? ErrorMessage { get; set; }
}
