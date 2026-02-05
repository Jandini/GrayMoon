using System.Text.Json.Serialization;
using GrayMoon.Agent.Models;

namespace GrayMoon.Agent.Jobs.Response;

public sealed class SyncRepositoryResponse
{
    [JsonPropertyName("version")]
    public string? Version { get; set; }

    [JsonPropertyName("branch")]
    public string? Branch { get; set; }

    [JsonPropertyName("projects")]
    public IReadOnlyList<CsProjFileInfo>? Projects { get; set; }
}
