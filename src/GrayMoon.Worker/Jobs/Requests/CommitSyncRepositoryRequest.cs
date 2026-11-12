using System.Text.Json.Serialization;

namespace GrayMoon.Worker.Jobs.Requests;

public sealed class CommitSyncRepositoryRequest : WorkspaceCommandRequest
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

    /// <summary>Optional Feature parent branch for ahead/behind; see <see cref="SyncRepositoryRequest.DivergenceBaseBranch"/>.</summary>
    [JsonPropertyName("divergenceBaseBranch")]
    public string? DivergenceBaseBranch { get; set; }

    /// <summary>
    /// For a read-only repository: only take incoming commits by fast-forward. Nothing is merged and nothing is
    /// pushed; a branch with commits of its own is refused before the pull.
    /// </summary>
    [JsonPropertyName("pullOnly")]
    public bool PullOnly { get; set; }
}
