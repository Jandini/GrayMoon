using System.Text.Json.Serialization;
namespace GrayMoon.Agent.Jobs.Requests;
/// <summary>Resolves current HEAD commit SHAs for the named repositories in one workspace.</summary>
public sealed class GetHeadCommitsRequest : WorkspaceCommandRequest
{
    [JsonPropertyName("workspaceName")] public string? WorkspaceName { get; set; }
    /// <summary>Repository folder names under the workspace root.</summary>
    [JsonPropertyName("repositoryNames")] public List<string>? RepositoryNames { get; set; }
}
