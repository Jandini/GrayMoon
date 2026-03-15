using System.Text.Json.Serialization;

namespace GrayMoon.Agent.Jobs.Requests;

public sealed class SyncToDefaultBranchRequest : WorkspaceCommandRequest
{
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
}
