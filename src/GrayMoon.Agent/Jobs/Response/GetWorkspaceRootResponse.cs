using System.Text.Json.Serialization;

namespace GrayMoon.Agent.Jobs.Response;

public sealed class GetWorkspaceRootResponse
{
    [JsonPropertyName("workspaceRoot")]
    public string WorkspaceRoot { get; set; } = string.Empty;
}
