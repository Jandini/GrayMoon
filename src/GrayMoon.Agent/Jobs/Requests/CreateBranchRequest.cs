using System.Text.Json.Serialization;

namespace GrayMoon.Agent.Jobs.Requests;

public sealed class CreateBranchRequest
{
    [JsonPropertyName("workspaceName")]
    public string? WorkspaceName { get; set; }

    [JsonPropertyName("repositoryName")]
    public string? RepositoryName { get; set; }

    [JsonPropertyName("newBranchName")]
    public string? NewBranchName { get; set; }

    [JsonPropertyName("baseBranchName")]
    public string? BaseBranchName { get; set; }
}
