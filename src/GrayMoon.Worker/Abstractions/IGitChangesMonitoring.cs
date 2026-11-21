namespace GrayMoon.Worker.Abstractions;

/// <summary>One repository folder the Worker holds a file watcher on.</summary>
/// <param name="Path">The watched working tree, normalized.</param>
/// <param name="WorkspaceId">The Workspace the App named when it asked about the folder; 0 when it is not known.</param>
/// <param name="RepositoryId">The repository the App named; 0 when it is not known.</param>
/// <param name="IsHealthy">True while the OS watchers are running.</param>
/// <param name="LeaseCount">Operations that currently hold the watcher.</param>
/// <param name="ExpiresAt">When the watcher is dropped if nothing renews it.</param>
public sealed record MonitoredRepository(
    string Path,
    int WorkspaceId,
    int RepositoryId,
    bool IsHealthy,
    int LeaseCount,
    DateTimeOffset ExpiresAt);

/// <summary>
/// The one answer to "which Workspaces is the Worker monitoring, and which folders". Every folder listed here has an
/// open directory handle of the Worker's on it, which is what stops the folder from being deleted or moved.
/// </summary>
public interface IGitChangesMonitoring
{
    /// <summary>The watched folders of <paramref name="workspaceId"/>, or of every Workspace when it is null.</summary>
    IReadOnlyList<MonitoredRepository> GetMonitored(int? workspaceId = null);
}
