using System.Text.Json.Serialization;
namespace GrayMoon.Agent.Jobs.Requests;
public sealed class CheckFileVersionsRequest : WorkspaceCommandRequest
{
    [JsonPropertyName("workspaceName")] public string? WorkspaceName { get; set; }
    [JsonPropertyName("files")] public List<CheckFileVersionsItem>? Files { get; set; }
}
public sealed class CheckFileVersionsItem
{
    [JsonPropertyName("repositoryName")] public string? RepositoryName { get; set; }
    [JsonPropertyName("filePath")] public string? FilePath { get; set; }
    [JsonPropertyName("pattern")] public string? Pattern { get; set; }
    /// <summary>Map of canonical token key to expected value (GitVersion, branch, or commit SHA).</summary>
    [JsonPropertyName("expectedValues")] public Dictionary<string, string>? ExpectedValues { get; set; }
}
