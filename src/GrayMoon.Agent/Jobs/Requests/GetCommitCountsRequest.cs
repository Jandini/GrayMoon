using System.Text.Json.Serialization;

namespace GrayMoon.Agent.Jobs.Requests;

public sealed class GetCommitCountsRequest : WorkspaceCommandRequest
{
    [JsonPropertyName("workspaceName")]
    public string? WorkspaceName { get; set; }

    [JsonPropertyName("repositoryName")]
    public string? RepositoryName { get; set; }

    /// <summary>Optional Feature parent branch for ahead/behind; see <see cref="SyncRepositoryRequest.DivergenceBaseBranch"/>.</summary>
    [JsonPropertyName("divergenceBaseBranch")]
    public string? DivergenceBaseBranch { get; set; }
}
