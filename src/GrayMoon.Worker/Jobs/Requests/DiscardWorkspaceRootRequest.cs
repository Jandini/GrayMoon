using System.Text.Json.Serialization;

namespace GrayMoon.Worker.Jobs.Requests;

/// <summary>
/// Rolls back the Workspace root folder of a restore that failed after the Workspace repository was attached.
/// The Worker deletes only what it can prove the restore created: an empty folder, or a clean clone of
/// <see cref="CloneUrl"/>.
/// </summary>
public sealed class DiscardWorkspaceRootRequest : WorkspaceCommandRequest
{
    /// <summary>Folder name under <see cref="WorkspaceCommandRequest.WorkspaceRoot"/>.</summary>
    [JsonPropertyName("workspaceName")]
    public string? WorkspaceName { get; set; }

    /// <summary>Remote URL the restore attached; the folder's origin must match it.</summary>
    [JsonPropertyName("cloneUrl")]
    public string? CloneUrl { get; set; }

    /// <summary>True when the (empty) folder existed before the restore: its contents are removed, the folder itself stays.</summary>
    [JsonPropertyName("keepFolder")]
    public bool KeepFolder { get; set; }
}
