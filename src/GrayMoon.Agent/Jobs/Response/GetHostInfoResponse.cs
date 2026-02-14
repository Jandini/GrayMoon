using System.Text.Json.Serialization;

namespace GrayMoon.Agent.Jobs.Response;

public sealed class GetHostInfoResponse
{
    [JsonPropertyName("workspaceRoot")]
    public string WorkspaceRoot { get; set; } = string.Empty;

    [JsonPropertyName("dotnetVersion")]
    public string? DotnetVersion { get; set; }

    [JsonPropertyName("gitVersion")]
    public string? GitVersion { get; set; }

    [JsonPropertyName("gitVersionToolVersion")]
    public string? GitVersionToolVersion { get; set; }
}
