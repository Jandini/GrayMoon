using System.Text.Json.Serialization;

namespace GrayMoon.Agent.Jobs.Requests;

public sealed class StageAndCommitRequest
{
    [JsonPropertyName("workspaceName")]
    public string? WorkspaceName { get; set; }

    [JsonPropertyName("repositoryName")]
    public string? RepositoryName { get; set; }

    [JsonPropertyName("commitMessage")]
    public string? CommitMessage { get; set; }

    [JsonPropertyName("pathsToStage")]
    public IReadOnlyList<string>? PathsToStage { get; set; }
}
