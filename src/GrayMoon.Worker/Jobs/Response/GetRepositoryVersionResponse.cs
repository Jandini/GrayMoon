using System.Text.Json.Serialization;

namespace GrayMoon.Worker.Jobs.Response;

public sealed class GetRepositoryVersionResponse
{
    [JsonPropertyName("exists")]
    public bool Exists { get; set; }

    [JsonPropertyName("version")]
    public string? Version { get; set; }

    [JsonPropertyName("branch")]
    public string? Branch { get; set; }
}
