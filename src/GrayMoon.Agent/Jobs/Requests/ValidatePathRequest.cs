using System.Text.Json.Serialization;

namespace GrayMoon.Agent.Jobs.Requests;

public sealed class ValidatePathRequest
{
    [JsonPropertyName("path")]
    public string? Path { get; set; }
}
