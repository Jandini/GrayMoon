using System.Text.Json.Serialization;
using GrayMoon.Worker.Models;

namespace GrayMoon.Worker.Jobs.Response;

public sealed class GetWorkspaceRepositoriesResponse
{
    /// <summary>
    /// Repository directory names within the workspace (kept for backward compatibility).
    /// </summary>
    [JsonPropertyName("repositories")]
    public string[] Repositories { get; set; } = [];

    /// <summary>
    /// Detailed repository info including origin URL for each repository directory.
    /// </summary>
    [JsonPropertyName("repositoryInfos")]
    public WorkspaceRepositoryInfo[] RepositoryInfos { get; set; } = [];
}
