using GrayMoon.Agent.Abstractions;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;

namespace GrayMoon.Agent.Commands;

/// <summary>
/// Handles post-commit and post-update hooks: re-runs GitVersion and gets commit counts.
/// No git fetch — uses the existing remote tracking refs from the last checkout/sync.
/// </summary>
public sealed class CommitHookSyncCommand(IGitService git, IHubConnectionProvider hubProvider, ILogger<CommitHookSyncCommand> logger)
{
    public async Task ExecuteAsync(INotifyJob payload, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(payload.RepositoryPath))
        {
            logger.LogWarning("CommitHookSync job missing repositoryPath");
            return;
        }

        var (versionResult, _) = await git.GetVersionAsync(payload.RepositoryPath, cancellationToken);
        var version = versionResult?.SemVer ?? versionResult?.FullSemVer ?? "-";
        var branch = versionResult?.BranchName ?? versionResult?.EscapedBranchName ?? "-";

        int? outgoing = null;
        int? incoming = null;
        if (branch != "-")
        {
            var (o, i) = await git.GetCommitCountsAsync(payload.RepositoryPath, branch, cancellationToken);
            outgoing = o;
            incoming = i;
        }

        var connection = hubProvider.Connection;
        if (connection?.State == HubConnectionState.Connected)
        {
            await connection.InvokeAsync("SyncCommand", payload.WorkspaceId, payload.RepositoryId, version, branch, outgoing, incoming, cancellationToken);
            logger.LogInformation("CommitHookSync sent: workspace={WorkspaceId}, repo={RepoId}, version={Version}, branch={Branch}, ↑{Outgoing} ↓{Incoming}",
                payload.WorkspaceId, payload.RepositoryId, version, branch, outgoing, incoming);
        }
        else
        {
            logger.LogWarning("Hub not connected, cannot send CommitHookSync SyncCommand");
        }
    }
}
