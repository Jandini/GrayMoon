using System.Text.Json.Serialization;

namespace GrayMoon.Worker.Jobs.Requests;

/// <summary>
/// Repositories of one Workspace that GrayMoon stops managing: the Worker lets go of its file watchers on their
/// folders and removes the Git hooks GrayMoon installed there. Nothing else on disk is touched. Paths are resolved
/// from the workspace root, folder name and repository names; the App never sends absolute paths.
/// </summary>
public sealed class DetachRepositoriesRequest : WorkspaceCommandRequest, IWorkspaceScopedRequest, IWorkspaceNamedRequest
{
    [JsonPropertyName("workspaceName")]
    public string? WorkspaceName { get; set; }

    [JsonPropertyName("workspaceId")]
    public int WorkspaceId { get; set; }

    [JsonPropertyName("repositories")]
    public List<DetachRepositoriesRepository> Repositories { get; set; } = [];

    /// <summary>
    /// True when the Workspace itself is being removed: every folder the Worker still watches for it is released too,
    /// and its cached capabilities are dropped.
    /// </summary>
    [JsonPropertyName("workspaceRemoved")]
    public bool WorkspaceRemoved { get; set; }
}

public sealed class DetachRepositoriesRepository : IRepositoryNamedRequest
{
    [JsonPropertyName("repositoryId")]
    public int RepositoryId { get; set; }

    [JsonPropertyName("repositoryName")]
    public string? RepositoryName { get; set; }
}
