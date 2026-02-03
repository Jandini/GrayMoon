using System.Text.Json.Serialization;

namespace GrayMoon.Agent.Jobs.Results;

public sealed class GetWorkspaceExistsResult
{
    [JsonPropertyName("exists")]
    public bool Exists { get; set; }
}
