using System.Text.Json.Serialization;

namespace GrayMoon.Worker.Jobs.Requests;

public sealed class GetWorkspaceRepositoriesRequest : WorkspaceCommandRequest, IWorkspaceNamedRequest
{
    [JsonPropertyName("workspaceName")]
    public string? WorkspaceName { get; set; }
}
