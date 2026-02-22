using System.Text.Json.Serialization;

namespace GrayMoon.Agent.Jobs.Requests;

public sealed class SearchFilesRequest
{
    [JsonPropertyName("workspaceName")]
    public string? WorkspaceName { get; set; }

    /// <summary>Optional. When null or empty, search all repositories in the workspace.</summary>
    [JsonPropertyName("repositoryName")]
    public string? RepositoryName { get; set; }

    /// <summary>File name pattern with wildcards (* and ?). e.g. "*.cs", "*.csproj".</summary>
    [JsonPropertyName("searchPattern")]
    public string? SearchPattern { get; set; }
}
