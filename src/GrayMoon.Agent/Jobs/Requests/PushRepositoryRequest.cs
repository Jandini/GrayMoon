using System.Text.Json.Serialization;

namespace GrayMoon.Agent.Jobs.Requests;

public sealed class PushRepositoryRequest : WorkspaceCommandRequest
{
    [JsonPropertyName("workspaceName")]
    public string? WorkspaceName { get; set; }

    [JsonPropertyName("repositoryId")]
    public int RepositoryId { get; set; }

    [JsonPropertyName("repositoryName")]
    public string? RepositoryName { get; set; }

    [JsonPropertyName("bearerToken")]
    public string? BearerToken { get; set; }

    [JsonPropertyName("workspaceId")]
    public int WorkspaceId { get; set; }

    /// <summary>Optional. When set, the agent uses this branch instead of resolving via GitVersion.</summary>
    [JsonPropertyName("branchName")]
    public string? BranchName { get; set; }
}
