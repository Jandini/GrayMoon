namespace GrayMoon.Abstractions.Notifications;

/// <summary>
/// Payload sent by the agent to the app when a repository sync completes (e.g. after commit/checkout/merge hook).
/// A single object avoids argument count/order mismatches and makes the contract explicit.
/// </summary>
public sealed class RepositorySyncNotification
{
    public int WorkspaceId { get; init; }
    public int RepositoryId { get; init; }
    public string Version { get; init; } = "-";
    public string Branch { get; init; } = "-";
    public int? OutgoingCommits { get; init; }
    public int? IncomingCommits { get; init; }
    public bool? HasUpstream { get; init; }
    public int? DefaultBranchBehind { get; init; }
    public int? DefaultBranchAhead { get; init; }
    public string? ErrorMessage { get; init; }
}
