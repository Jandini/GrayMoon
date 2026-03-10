using System.Text.Json.Serialization;

namespace GrayMoon.Agent.Jobs.Requests;

public sealed class DeleteBranchRequest : WorkspaceCommandRequest
{
    [JsonPropertyName("workspaceName")]
    public string? WorkspaceName { get; set; }

    [JsonPropertyName("repositoryName")]
    public string? RepositoryName { get; set; }

    [JsonPropertyName("branchName")]
    public string? BranchName { get; set; }

    [JsonPropertyName("isRemote")]
    public bool IsRemote { get; set; }

    /// <summary>When true and deleting a local branch, uses git branch -D after -d failed (not fully merged).</summary>
    [JsonPropertyName("force")]
    public bool Force { get; set; }
}
