using System.Text.Json.Serialization;

namespace GrayMoon.Agent.Jobs.Requests;

public sealed class RefreshRepositoryVersionRequest : WorkspaceCommandRequest
{
    [JsonPropertyName("workspaceName")]
    public string? WorkspaceName { get; set; }

    [JsonPropertyName("repositoryName")]
    public string? RepositoryName { get; set; }

    [JsonPropertyName("repositoryId")]
    public int RepositoryId { get; set; }

    /// <summary>Optional Feature parent branch for ahead/behind; see <see cref="SyncRepositoryRequest.DivergenceBaseBranch"/>.</summary>
    [JsonPropertyName("divergenceBaseBranch")]
    public string? DivergenceBaseBranch { get; set; }
}
