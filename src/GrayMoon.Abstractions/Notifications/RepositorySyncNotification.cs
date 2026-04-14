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
    public List<RepositorySyncProjectNotification>? Projects { get; init; }
    public string? ErrorMessage { get; init; }
}

public sealed class RepositorySyncProjectNotification
{
    public string Name { get; init; } = "";
    public int ProjectType { get; init; }
    public string? ProjectPath { get; init; }
    public string? TargetFramework { get; init; }
    public string? PackageId { get; init; }
    public List<RepositorySyncPackageReferenceNotification>? PackageReferences { get; init; }
}

public sealed class RepositorySyncPackageReferenceNotification
{
    public string Name { get; init; } = "";
    public string? Version { get; init; }
}
