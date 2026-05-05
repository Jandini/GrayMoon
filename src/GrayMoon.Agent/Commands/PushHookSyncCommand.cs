using GrayMoon.Abstractions.Agent;
using GrayMoon.Abstractions.Notifications;
using GrayMoon.Agent.Abstractions;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;

namespace GrayMoon.Agent.Commands;

/// <summary>
/// Handles pre-push hooks: re-runs GitVersion to capture the version/branch at push time.
/// Commit counts and upstream state are intentionally omitted — this hook fires BEFORE the
/// push completes, so any counts read here are stale (still show the commits about to be
/// pushed). Sending them via SyncCommand would overwrite the correct post-push values
/// written by UpdateCommitCountsAndUpstreamAfterPushAsync, causing a race condition where
/// the grid briefly shows the correct 0 and then flips back to the pre-push count.
/// </summary>
public sealed class PushHookSyncCommand(IGitService git, IHubConnectionProvider hubProvider, ILogger<PushHookSyncCommand> logger)
{
    public async Task ExecuteAsync(INotifyJob payload, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(payload.RepositoryPath))
        {
            logger.LogWarning("PushHookSync job missing repositoryPath");
            return;
        }

        var (versionResult, _) = await git.GetVersionAsync(payload.RepositoryPath, cancellationToken);
        var version = versionResult?.InformationalVersion ?? "-";
        var branch = versionResult?.BranchName ?? versionResult?.EscapedBranchName ?? "-";

        var connection = hubProvider.Connection;
        if (connection?.State == HubConnectionState.Connected)
        {
            // OutgoingCommits, IncomingCommits, HasUpstream and DefaultBranch deltas are all null
            // so SyncCommandHandler's HasValue guards skip those columns — the app-side
            // UpdateCommitCountsAndUpstreamAfterPushAsync writes the authoritative post-push counts.
            var notification = new RepositorySyncNotification
            {
                WorkspaceId = payload.WorkspaceId,
                RepositoryId = payload.RepositoryId,
                Version = version,
                Branch = branch,
                ErrorMessage = null
            };
            await connection.InvokeAsync(AgentHubMethods.SyncCommand, notification, cancellationToken);
            logger.LogInformation("PushHookSync sent: workspace={WorkspaceId}, repo={RepoId}, version={Version}, branch={Branch}",
                payload.WorkspaceId, payload.RepositoryId, version, branch);
        }
        else
        {
            logger.LogWarning("Hub not connected, cannot send PushHookSync SyncCommand");
        }
    }
}
