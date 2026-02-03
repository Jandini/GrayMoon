using System.Text.Json.Serialization;

namespace GrayMoon.Agent.Jobs.Results;

public sealed class SyncRepositoryResult
{
    [JsonPropertyName("version")]
    public string? Version { get; set; }

    [JsonPropertyName("branch")]
    public string? Branch { get; set; }

    [JsonPropertyName("wasCloned")]
    public bool WasCloned { get; set; }
}
