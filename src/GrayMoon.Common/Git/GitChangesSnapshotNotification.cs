using System.Text.Json.Serialization;

namespace GrayMoon.Common.Git;

/// <summary>
/// Payload for the unsolicited Worker to App <c>GitChangesSnapshotUpdated</c> SignalR push (see
/// <c>WorkerHubMethods.GitChangesSnapshotUpdated</c> in GrayMoon.Abstractions). Lives in GrayMoon.Common,
/// not GrayMoon.Worker, because both the Worker (sender) and the App (receiver) already reference Common -
/// no per-process DTO duplication needed for this one, unlike the Worker-local command request/response types.
/// </summary>
public sealed class GitChangesSnapshotNotification
{
    [JsonPropertyName("workspaceId")]
    public int WorkspaceId { get; init; }

    [JsonPropertyName("repositoryId")]
    public int RepositoryId { get; init; }

    /// <summary>Absolute checkout/worktree path that produced the snapshot; App attributes to a Feature context.</summary>
    [JsonPropertyName("repositoryPath")]
    public string? RepositoryPath { get; init; }

    [JsonPropertyName("snapshot")]
    public required GitChangeSnapshot Snapshot { get; init; }
}
