using System.Text.Json.Serialization;

namespace GrayMoon.Worker.Jobs.Requests;

/// <summary>
/// Attaches a remote repository to the Workspace root folder itself (the Workspace repository), either by cloning
/// into an empty root or by initializing Git in a non-empty root and tracking the remote default branch.
/// </summary>
public sealed class AttachWorkspaceRepositoryRequest : WorkspaceCommandRequest
{
    /// <summary>Folder name under <see cref="WorkspaceCommandRequest.WorkspaceRoot"/>.</summary>
    [JsonPropertyName("workspaceName")]
    public string? WorkspaceName { get; set; }

    /// <summary>Remote URL of the repository.</summary>
    [JsonPropertyName("cloneUrl")]
    public string? CloneUrl { get; set; }

    /// <summary>Connector token. Used at runtime only and never persisted.</summary>
    [JsonPropertyName("bearerToken")]
    public string? BearerToken { get; set; }

    /// <summary>Identifies the Workspace in the sync hooks the Worker installs.</summary>
    [JsonPropertyName("workspaceId")]
    public int WorkspaceId { get; set; }

    /// <summary>Identifies the repository in the sync hooks the Worker installs.</summary>
    [JsonPropertyName("repositoryId")]
    public int RepositoryId { get; set; }
}
