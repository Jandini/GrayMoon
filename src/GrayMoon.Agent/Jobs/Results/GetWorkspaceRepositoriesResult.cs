using System.Text.Json.Serialization;

namespace GrayMoon.Agent.Jobs.Results;

public sealed class GetWorkspaceRepositoriesResult
{
    [JsonPropertyName("repositories")]
    public string[] Repositories { get; set; } = [];
}
