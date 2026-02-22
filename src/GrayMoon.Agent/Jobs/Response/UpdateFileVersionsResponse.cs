using System.Text.Json.Serialization;

namespace GrayMoon.Agent.Jobs.Response;

public sealed class UpdateFileVersionsResponse
{
    [JsonPropertyName("updatedCount")] public int UpdatedCount { get; set; }
    [JsonPropertyName("errorMessage")] public string? ErrorMessage { get; set; }
}
