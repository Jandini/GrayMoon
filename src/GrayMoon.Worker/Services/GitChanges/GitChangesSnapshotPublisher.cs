using System.Diagnostics;
using GrayMoon.Abstractions.Worker;
using GrayMoon.Worker.Abstractions;
using GrayMoon.Common.Git;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

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
    private readonly IOptions<GitChangesOptions> _gitChangesOptions;
    private readonly ILogger<GitChangesSnapshotPublisher> _logger;

    public GitChangesSnapshotPublisher(
        GitStatusRefreshCoordinator coordinator,
        GitChangesRepositoryRegistry registry,
        IHubConnectionProvider hubProvider,
        IOptions<GitChangesOptions> gitChangesOptions,
        ILogger<GitChangesSnapshotPublisher> logger)
    {
        _coordinator = coordinator;
        _registry = registry;
        _hubProvider = hubProvider;
        _gitChangesOptions = gitChangesOptions;
        _logger = logger;
        _coordinator.SnapshotReady += OnSnapshotReady;
    }

    private void OnSnapshotReady(string repoPath, GitChangeSnapshot snapshot)
    {
        if (!_registry.TryGet(repoPath, out var workspaceId, out var repositoryId))
        {
            return;
        }

        _ = PublishAsync(workspaceId, repositoryId, repoPath, snapshot);
    }

    private async Task PublishAsync(int workspaceId, int repositoryId, string repositoryPath, GitChangeSnapshot snapshot)
    {
        var connection = _hubProvider.Connection;
        if (connection?.State != HubConnectionState.Connected)
        {
            return;
        }

        var options = _gitChangesOptions.Value;
        var pathCount = snapshot.Changes.Count;
        var buildStarted = Stopwatch.GetTimestamp();
        var notification = new GitChangesSnapshotNotification
        {
            WorkspaceId = workspaceId,
            RepositoryId = repositoryId,
            RepositoryPath = repositoryPath,
            Snapshot = snapshot,
        };
        var buildMs = GitChangesBench.ElapsedMs(buildStarted);

        var serializeStarted = Stopwatch.GetTimestamp();
        var payloadUtf8Bytes = GitChangesBench.EstimateUtf8JsonBytes(notification);
        var serializeMs = GitChangesBench.ElapsedMs(serializeStarted);
        var bytesPerEntry = GitChangesBench.BytesPerEntry(payloadUtf8Bytes, pathCount);

        GitChangesBench.Log(
            _logger,
            options,
            "snapshot publish prepare",
            pathCount,
            buildMs + serializeMs,
            ("BuildMs", buildMs),
            ("SerializeMs", serializeMs),
            ("PayloadUtf8Bytes", payloadUtf8Bytes),
            ("BytesPerEntry", bytesPerEntry),
            ("PathCount", pathCount),
            ("Chunked", false),
            ("ChunkCount", 0),
            ("WorkspaceId", workspaceId),
            ("RepositoryId", repositoryId),
            ("Version", snapshot.Version));

        var sendStarted = Stopwatch.GetTimestamp();
        try
        {
            await connection.InvokeAsync(WorkerHubMethods.GitChangesSnapshotUpdated, notification);
            var sendMs = GitChangesBench.ElapsedMs(sendStarted);
            GitChangesBench.Log(
                _logger,
                options,
                "snapshot send result",
                pathCount,
                sendMs,
                ("SendMs", sendMs),
                ("Success", true),
                ("HubSizeRejected", false),
                ("PayloadUtf8Bytes", payloadUtf8Bytes),
                ("PathCount", pathCount),
                ("WorkspaceId", workspaceId),
                ("RepositoryId", repositoryId),
                ("Version", snapshot.Version));
        }
        catch (Exception ex)
        {
            var sendMs = GitChangesBench.ElapsedMs(sendStarted);
            var hubSizeRejected = IsLikelySignalRMessageSizeOrPayloadRejection(ex);
            GitChangesBench.Log(
                _logger,
                options,
                "snapshot send result",
                pathCount,
                sendMs,
                ("SendMs", sendMs),
                ("Success", false),
                ("HubSizeRejected", hubSizeRejected),
                ("PayloadUtf8Bytes", payloadUtf8Bytes),
                ("PathCount", pathCount),
                ("WorkspaceId", workspaceId),
                ("RepositoryId", repositoryId),
                ("Version", snapshot.Version));

            _logger.LogWarning(
                ex,
                "Failed to publish Git Changes snapshot update for workspace {WorkspaceId} repository {RepositoryId}",
                workspaceId,
                repositoryId);
        }
    }

    /// <summary>Same heuristics as <c>ResponseCommandSendPipeline</c> - hub size rejections must not look like transient send failures in the bench line.</summary>
    private static bool IsLikelySignalRMessageSizeOrPayloadRejection(Exception ex)
    {
        for (var e = ex; e != null; e = e.InnerException)
        {
            var m = e.Message;
            if (string.IsNullOrEmpty(m))
                continue;

            if (m.Contains("Maximum message size", StringComparison.OrdinalIgnoreCase))
                return true;
            if (m.Contains("MaximumReceiveMessageSize", StringComparison.OrdinalIgnoreCase))
                return true;
            if (m.Contains("32768", StringComparison.Ordinal))
                return true;
            if (m.Contains("maximum message size of", StringComparison.OrdinalIgnoreCase)
                && m.Contains("exceeded", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Dispose();
        return Task.CompletedTask;
    }

    public void Dispose() => _coordinator.SnapshotReady -= OnSnapshotReady;
}
