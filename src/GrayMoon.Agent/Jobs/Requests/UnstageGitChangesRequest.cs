using System.Text.Json.Serialization;

namespace GrayMoon.Agent.Jobs.Requests;

public sealed class UnstageGitChangesRequest : WorkspaceCommandRequest
{
    [JsonPropertyName("workspaceName")]
    public string? WorkspaceName { get; set; }

    [JsonPropertyName("repositoryName")]
    public string? RepositoryName { get; set; }

    /// <summary><see cref="GrayMoon.Common.Git.GitChangeOperationScope"/> as an int.</summary>
    [JsonPropertyName("scope")]
    public int Scope { get; set; }

    [JsonPropertyName("paths")]
    public IReadOnlyList<string>? Paths { get; set; }
}
