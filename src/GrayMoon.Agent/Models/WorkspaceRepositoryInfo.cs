using System.Text.Json.Serialization;

namespace GrayMoon.Agent.Models;

public sealed class WorkspaceRepositoryInfo
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("originUrl")]
    public string? OriginUrl { get; set; }
}

