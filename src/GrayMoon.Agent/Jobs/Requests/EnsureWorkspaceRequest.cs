using System.Text.Json.Serialization;

namespace GrayMoon.Agent.Jobs.Requests;

public sealed class EnsureWorkspaceRequest : WorkspaceCommandRequest
{
    [JsonPropertyName("workspaceName")]
    public string? WorkspaceName { get; set; }
}
