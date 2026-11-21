using System.Text.Json.Serialization;
using GrayMoon.Worker.Models;

namespace GrayMoon.Worker.Jobs.Response;

public sealed class GetGitChangesMonitoringResponse
{
    [JsonPropertyName("repositories")]
    public List<MonitoredRepositoryInfo> Repositories { get; set; } = [];
}

public sealed class MonitoredRepositoryInfo
{
    [JsonPropertyName("path")]
    public string? Path { get; set; }

    [JsonPropertyName("workspaceId")]
    public int WorkspaceId { get; set; }

    [JsonPropertyName("repositoryId")]
    public int RepositoryId { get; set; }

    [JsonPropertyName("isHealthy")]
    public bool IsHealthy { get; set; }

    [JsonPropertyName("leaseCount")]
    public int LeaseCount { get; set; }

    [JsonPropertyName("expiresAt")]
    public DateTimeOffset ExpiresAt { get; set; }
}

public sealed class ListWorkspaceRootFoldersResponse
{
    /// <summary>Folder names (not paths) directly under the workspace root; empty when the root does not exist.</summary>
    [JsonPropertyName("folders")]
    public List<string> Folders { get; set; } = [];
}

public sealed class InspectWorkspaceFolderResponse
{
    [JsonPropertyName("exists")]
    public bool Exists { get; set; }

    /// <summary>The folder itself, when it is a Git repository. Null when it is not.</summary>
    [JsonPropertyName("rootRepository")]
    public WorkspaceRepositoryInfo? RootRepository { get; set; }

    /// <summary>Sub-folders that are Git repositories, with their origin URL.</summary>
    [JsonPropertyName("repositories")]
    public List<WorkspaceRepositoryInfo> Repositories { get; set; } = [];

    /// <summary>True when the Workspace definition file is in the folder.</summary>
    [JsonPropertyName("definitionFound")]
    public bool DefinitionFound { get; set; }

    /// <summary>The text of the Workspace definition file; null when it is missing or could not be read.</summary>
    [JsonPropertyName("definitionContent")]
    public string? DefinitionContent { get; set; }

    /// <summary>Why the definition file could not be read (too large, access denied).</summary>
    [JsonPropertyName("definitionError")]
    public string? DefinitionError { get; set; }
}
