using System.Text.Json.Serialization;

namespace GrayMoon.Agent.Jobs.Response;

public sealed class CheckoutTagResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("currentTag")]
    public string? CurrentTag { get; set; }

    [JsonPropertyName("errorMessage")]
    public string? ErrorMessage { get; set; }
}
