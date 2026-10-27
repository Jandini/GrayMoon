using GrayMoon.Abstractions.Worker;
using GrayMoon.Worker.Abstractions;
using GrayMoon.Common.Git;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GrayMoon.Worker.Services.GitChanges;

/// <summary>
/// Pushes an unsolicited <see cref="WorkerHubMethods.GitChangesSnapshotUpdated"/> event to the App whenever
/// the refresh coordinator produces a new snapshot for a repository the App has previously asked about
/// (i.e. one with a <see cref="GitChangesRepositoryRegistry"/> entry). Mirrors the existing hook-driven
/// <c>SyncCommand</c> push pattern used elsewhere in the Worker. Registered as an <see cref="IHostedService"/>
/// purely so the host constructs it eagerly at startup - all the actual work happens via the constructor's
/// event subscription, there is no async start/stop behavior.
/// </summary>
public sealed class GitChangesSnapshotPublisher : IHostedService, IDisposable
{
    private readonly GitStatusRefreshCoordinator _coordinator;
    private readonly GitChangesRepositoryRegistry _registry;
    private readonly IHubConnectionProvider _hubProvider;
    private readonly ILogger<GitChangesSnapshotPublisher> _logger;
    private long _droppedWhileOffline;

    public GitChangesSnapshotPublisher(
        GitStatusRefreshCoordinator coordinator,
        GitChangesRepositoryRegistry registry,
        IHubConnectionProvider hubProvider,
        ILogger<GitChangesSnapshotPublisher> logger)
    {
        _coordinator = coordinator;
        _registry = registry;
        _hubProvider = hubProvider;
        _logger = logger;
        _coordinator.SnapshotReady += OnSnapshotReady;
    }

    private void OnSnapshotReady(string repoPath, GitChangeSnapshot snapshot)
    {
        if (!_registry.TryGet(repoPath, out var workspaceId, out var repositoryId))
        {
            _logger.LogDebug("[GCDiag] snapshot not published (path not registered with the App): {RepoPath} version={Version}", repoPath, snapshot.Version);
            return;
        }

        _ = PublishAsync(workspaceId, repositoryId, repoPath, snapshot);
    }

    private async Task PublishAsync(int workspaceId, int repositoryId, string repositoryPath, GitChangeSnapshot snapshot)
    {
        var connection = _hubProvider.Connection;
        if (connection?.State != HubConnectionState.Connected)
        {
            var dropped = Interlocked.Increment(ref _droppedWhileOffline);
            if (dropped == 1 || dropped % 25 == 0)
            {
                _logger.LogWarning(
                    "[GCDiag] snapshot dropped because the App connection is not connected (totalDropped={Dropped}): workspace={WorkspaceId} repo={RepositoryId} version={Version}. Nothing is buffered; the view is stale until a later scan",
                    dropped, workspaceId, repositoryId, snapshot.Version);
            }

            return;
        }

        var notification = new GitChangesSnapshotNotification
        {
            WorkspaceId = workspaceId,
            RepositoryId = repositoryId,
            RepositoryPath = repositoryPath,
            Snapshot = snapshot,
        };

        try
        {
            await connection.InvokeAsync(WorkerHubMethods.GitChangesSnapshotUpdated, notification);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to publish Git Changes snapshot update for workspace {WorkspaceId} repository {RepositoryId}",
                workspaceId,
                repositoryId);
        }
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Dispose();
        return Task.CompletedTask;
    }

    public void Dispose() => _coordinator.SnapshotReady -= OnSnapshotReady;
}
