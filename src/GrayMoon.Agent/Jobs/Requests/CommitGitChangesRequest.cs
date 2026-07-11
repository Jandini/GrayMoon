using System.Text.Json.Serialization;

namespace GrayMoon.Agent.Jobs.Requests;

public sealed class CommitGitChangesRequest : WorkspaceCommandRequest
{
    [JsonPropertyName("workspaceName")]
    public string? WorkspaceName { get; set; }

    [JsonPropertyName("repositoryName")]
    public string? RepositoryName { get; set; }

    [JsonPropertyName("commitMessage")]
    public string? CommitMessage { get; set; }

    /// <summary>Commit All (stage every change first) vs Commit Staged (commit only what is already staged).</summary>
    [JsonPropertyName("stageAllFirst")]
    public bool StageAllFirst { get; set; }
}
