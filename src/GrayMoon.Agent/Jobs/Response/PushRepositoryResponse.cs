using System.Text.Json.Serialization;

namespace GrayMoon.Agent.Jobs.Response;

public sealed class PushRepositoryResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("errorMessage")]
    public string? ErrorMessage { get; set; }
}
