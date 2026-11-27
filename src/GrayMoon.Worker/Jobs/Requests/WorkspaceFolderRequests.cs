using System.Text.Json.Serialization;

namespace GrayMoon.Worker.Jobs.Requests;

/// <summary>Asks which folders the Worker holds file watchers on. <see cref="WorkspaceId"/> null means every Workspace.</summary>
public sealed class GetGitChangesMonitoringRequest
{
    [JsonPropertyName("workspaceId")]
    public int? WorkspaceId { get; set; }
}

/// <summary>Asks for the folders directly under the workspace root.</summary>
public sealed class ListWorkspaceRootFoldersRequest : WorkspaceCommandRequest
{
}

/// <summary>Asks what one folder under the workspace root holds. Reads only.</summary>
public sealed class InspectWorkspaceFolderRequest : WorkspaceCommandRequest, IWorkspaceNamedRequest
{
    [JsonPropertyName("workspaceName")]
    public string? WorkspaceName { get; set; }
}
