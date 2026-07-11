using System.Text.Json.Serialization;

namespace GrayMoon.Agent.Jobs.Requests;

public sealed class GetGitFileDiffRequest : WorkspaceCommandRequest
{
    [JsonPropertyName("workspaceName")]
    public string? WorkspaceName { get; set; }

    [JsonPropertyName("repositoryName")]
    public string? RepositoryName { get; set; }

    [JsonPropertyName("path")]
    public string? Path { get; set; }

    /// <summary><see cref="GrayMoon.Common.Git.GitDiffComparison"/> as an int (0 = Staged, 1 = Unstaged).</summary>
    [JsonPropertyName("comparison")]
    public int Comparison { get; set; }
}
