using System.Text.Json.Serialization;

namespace GrayMoon.Agent.Jobs.Requests;

public sealed class GetGitChangeStatusRequest : WorkspaceCommandRequest
{
    [JsonPropertyName("workspaceName")]
    public string? WorkspaceName { get; set; }

    [JsonPropertyName("repositoryName")]
    public string? RepositoryName { get; set; }

    /// <summary>Database workspace ID, echoed back on watcher-driven push notifications for this
    /// repository so the App can attribute them without a request/response round trip.</summary>
    [JsonPropertyName("workspaceId")]
    public int WorkspaceId { get; set; }

    /// <summary>Database repository ID, echoed back on watcher-driven push notifications.</summary>
    [JsonPropertyName("repositoryId")]
    public int RepositoryId { get; set; }
}
