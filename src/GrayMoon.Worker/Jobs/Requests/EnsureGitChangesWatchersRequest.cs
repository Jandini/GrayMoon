using System.Text.Json.Serialization;

namespace GrayMoon.Worker.Jobs.Requests;

/// <summary>
/// Keeps the file watchers of several repositories of one workspace context alive without running git.
/// Paths are resolved from the workspace root, folder name and repository names exactly as
/// <c>GetGitChangeStatus</c> does; the App never sends raw absolute paths.
/// </summary>
public sealed class EnsureGitChangesWatchersRequest : WorkspaceCommandRequest
{
    [JsonPropertyName("workspaceName")]
    public string? WorkspaceName { get; set; }

    /// <summary>Database workspace ID, used for push attribution of watcher-driven snapshots.</summary>
    [JsonPropertyName("workspaceId")]
    public int WorkspaceId { get; set; }

    [JsonPropertyName("repositories")]
    public List<EnsureGitChangesWatchersRepository> Repositories { get; set; } = [];
}

public sealed class EnsureGitChangesWatchersRepository
{
    [JsonPropertyName("repositoryId")]
    public int RepositoryId { get; set; }

    [JsonPropertyName("repositoryName")]
    public string? RepositoryName { get; set; }
}
