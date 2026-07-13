using System.Text.Json.Serialization;

namespace GrayMoon.Common.Git;

/// <summary>
/// Payload for the unsolicited Agent to App <c>GitChangesSnapshotUpdated</c> SignalR push (see
/// <c>AgentHubMethods.GitChangesSnapshotUpdated</c> in GrayMoon.Abstractions). Lives in GrayMoon.Common,
/// not GrayMoon.Agent, because both the Agent (sender) and the App (receiver) already reference Common -
/// no per-process DTO duplication needed for this one, unlike the Agent-local command request/response types.
/// </summary>
public sealed class GitChangesSnapshotNotification
{
    [JsonPropertyName("workspaceId")]
    public int WorkspaceId { get; init; }

    [JsonPropertyName("repositoryId")]
    public int RepositoryId { get; init; }

    [JsonPropertyName("snapshot")]
    public required GitChangeSnapshot Snapshot { get; init; }
}
