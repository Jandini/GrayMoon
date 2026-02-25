using System.Text.Json.Serialization;

namespace GrayMoon.Agent.Jobs.Response;

public sealed class GetHostInfoResponse
{    [JsonPropertyName("dotnetVersion")]
    public string? DotnetVersion { get; set; }

    [JsonPropertyName("gitVersion")]
    public string? GitVersion { get; set; }

    [JsonPropertyName("gitVersionToolVersion")]
    public string? GitVersionToolVersion { get; set; }
}
