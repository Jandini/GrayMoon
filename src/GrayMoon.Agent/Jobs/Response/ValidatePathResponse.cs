using System.Text.Json.Serialization;

namespace GrayMoon.Agent.Jobs.Response;

public sealed class ValidatePathResponse
{
    [JsonPropertyName("isValid")]
    public bool IsValid { get; set; }

    [JsonPropertyName("errorMessage")]
    public string? ErrorMessage { get; set; }
}
