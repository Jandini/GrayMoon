using System.Text.Json.Serialization;

namespace GrayMoon.Agent.Jobs.Requests;

public sealed class GetWorkspaceRepositoriesRequest
{
    [JsonPropertyName("workspaceName")]
    public string? WorkspaceName { get; set; }
}
