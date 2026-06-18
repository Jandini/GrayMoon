using System.Text.Json.Serialization;

namespace GrayMoon.Agent.Jobs.Requests;

public sealed class SelfUpdateRequest
{
    [JsonPropertyName("installUrl")]
    public string? InstallUrl { get; set; }
}
