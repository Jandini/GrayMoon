using System.Text.Json.Serialization;

namespace GrayMoon.Agent.Jobs.Response;

public sealed class SyncRepositoryResponse
{
    [JsonPropertyName("version")]
    public string? Version { get; set; }

    [JsonPropertyName("branch")]
    public string? Branch { get; set; }

    [JsonPropertyName("wasCloned")]
    public bool WasCloned { get; set; }

    [JsonPropertyName("projects")]
    public int Projects { get; set; }
}
