using System.Text.Json.Serialization;

namespace GrayMoon.Worker.Jobs.Requests;

public sealed class ReturnToDefaultBranchRequest : WorkspaceCommandRequest
{
    /// <summary>Optional. Lets the worker remember the workspace's capabilities for its git hooks.</summary>
    [JsonPropertyName("workspaceId")]
    public int WorkspaceId { get; set; }

    [JsonPropertyName("workspaceName")]
    public string? WorkspaceName { get; set; }

    [JsonPropertyName("repositoryName")]
    public string? RepositoryName { get; set; }

    [JsonPropertyName("currentBranchName")]
    public string? CurrentBranchName { get; set; }

    [JsonPropertyName("bearerToken")]
    public string? BearerToken { get; set; }

    /// <summary>When true, delete the previous local branch with -D (force). Set from PR merged status by the App.</summary>
    [JsonPropertyName("forceDeleteLocalBranch")]
    public bool ForceDeleteLocalBranch { get; set; }

    /// <summary>When true, delete the remote branch before fetching. Set from user confirmation in the UI.</summary>
    [JsonPropertyName("deleteRemoteBranch")]
    public bool DeleteRemoteBranch { get; set; }
}
