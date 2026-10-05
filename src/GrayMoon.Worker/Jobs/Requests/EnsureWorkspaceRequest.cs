using System.Text.Json.Serialization;

namespace GrayMoon.Worker.Jobs.Requests;

public sealed class EnsureWorkspaceRequest : WorkspaceCommandRequest
{
    [JsonPropertyName("workspaceName")]
    public string? WorkspaceName { get; set; }
}
