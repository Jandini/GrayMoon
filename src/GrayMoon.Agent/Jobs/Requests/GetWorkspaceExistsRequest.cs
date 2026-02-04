using System.Text.Json.Serialization;

namespace GrayMoon.Agent.Jobs.Requests;

public sealed class GetWorkspaceExistsRequest
{
    [JsonPropertyName("workspaceName")]
    public string? WorkspaceName { get; set; }
}
