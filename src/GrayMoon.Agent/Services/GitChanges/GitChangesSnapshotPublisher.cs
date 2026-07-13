using GrayMoon.Abstractions.Agent;
using GrayMoon.Agent.Abstractions;
using GrayMoon.Common.Git;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GrayMoon.Agent.Services.GitChanges;

/// <summary>
/// Pushes an unsolicited <see cref="AgentHubMethods.GitChangesSnapshotUpdated"/> event to the App whenever
/// the refresh coordinator produces a new snapshot for a repository the App has previously asked about
/// (i.e. one with a <see cref="GitChangesRepositoryRegistry"/> entry). Mirrors the existing hook-driven
/// <c>SyncCommand</c> push pattern used elsewhere in the Agent. Registered as an <see cref="IHostedService"/>
/// purely so the host constructs it eagerly at startup - all the actual work happens via the constructor's
/// event subscription, there is no async start/stop behavior.
/// </summary>
public sealed class GitChangesSnapshotPublisher : IHostedService, IDisposable
{
    private readonly GitStatusRefreshCoordinator _coordinator;
    private readonly GitChangesRepositoryRegistry _registry;
    private readonly IHubConnectionProvider _hubProvider;
    private readonly ILogger<GitChangesSnapshotPublisher> _logger;

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
            return;
        }

        _ = PublishAsync(workspaceId, repositoryId, snapshot);
    }

    private async Task PublishAsync(int workspaceId, int repositoryId, GitChangeSnapshot snapshot)
    {
        var connection = _hubProvider.Connection;
        if (connection?.State != HubConnectionState.Connected)
        {
            return;
        }

        try
        {
            var notification = new GitChangesSnapshotNotification
            {
                WorkspaceId = workspaceId,
                RepositoryId = repositoryId,
                Snapshot = snapshot,
            };
            await connection.InvokeAsync(AgentHubMethods.GitChangesSnapshotUpdated, notification);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to publish Git Changes snapshot update for workspace {WorkspaceId} repository {RepositoryId}", workspaceId, repositoryId);
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
