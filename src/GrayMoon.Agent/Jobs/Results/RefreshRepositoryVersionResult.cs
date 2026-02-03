using System.Text.Json.Serialization;

namespace GrayMoon.Agent.Jobs.Results;

public sealed class RefreshRepositoryVersionResult
{
    [JsonPropertyName("version")]
    public string? Version { get; set; }

    [JsonPropertyName("branch")]
    public string? Branch { get; set; }
}
