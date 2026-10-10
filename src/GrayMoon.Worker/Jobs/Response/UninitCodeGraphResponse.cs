using System.Text.Json.Serialization;

namespace GrayMoon.Worker.Jobs.Response;

public sealed class UninitCodeGraphResponse
{
    /// <summary><c>Removed</c>, <c>NotInitialized</c>, <c>NotInstalled</c> or <c>Failed</c>.</summary>
    [JsonPropertyName("outcome")]
    public string? Outcome { get; set; }

    /// <summary>Why the index was not removed; null otherwise.</summary>
    [JsonPropertyName("message")]
    public string? Message { get; set; }
}
