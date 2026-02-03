using System.Text.Json.Serialization;

namespace GrayMoon.Agent.Jobs.Results;

public sealed class GetRepositoryVersionResult
{
    [JsonPropertyName("exists")]
    public bool Exists { get; set; }

    [JsonPropertyName("version")]
    public string? Version { get; set; }

    [JsonPropertyName("branch")]
    public string? Branch { get; set; }
}
