using System.Text.Json.Serialization;

namespace GrayMoon.Agent.Jobs.Response;

public sealed class GetWorkspaceRepositoriesResponse
{
    [JsonPropertyName("repositories")]
    public string[] Repositories { get; set; } = [];
}
