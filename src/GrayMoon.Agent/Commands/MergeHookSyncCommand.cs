using GrayMoon.Agent.Abstractions;
using GrayMoon.Agent.Hub;
using GrayMoon.Agent.Services;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;

namespace GrayMoon.Agent.Commands;

/// <summary>
/// Handles post-merge hooks: re-runs GitVersion and gets commit counts.
/// No git fetch — the merge already brought remote changes in; existing remote tracking refs are current enough.
/// </summary>
public sealed class MergeHookSyncCommand(IGitService git, IHubConnectionProvider hubProvider, ILogger<MergeHookSyncCommand> logger)
{
    public async Task ExecuteAsync(INotifyJob payload, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(payload.RepositoryPath))
        {
            logger.LogWarning("MergeHookSync job missing repositoryPath");
            return;
        }

        var (versionResult, _) = await git.GetVersionAsync(payload.RepositoryPath, cancellationToken);
        var version = versionResult?.SemVer ?? versionResult?.FullSemVer ?? "-";
        var branch = versionResult?.BranchName ?? versionResult?.EscapedBranchName ?? "-";

        int? outgoing = null;
        int? incoming = null;
        if (branch != "-")
        {
            var (o, i, _) = await git.GetCommitCountsAsync(payload.RepositoryPath, branch, cancellationToken);
            outgoing = o;
            incoming = i;
        }

        bool? hasUpstream = null;
        if (branch != "-")
        {
            var remoteBranches = await git.GetRemoteBranchesAsync(payload.RepositoryPath, cancellationToken);
            hasUpstream = remoteBranches.Any(r => string.Equals(r, branch, StringComparison.OrdinalIgnoreCase));
        }

        var connection = hubProvider.Connection;
        if (connection?.State == HubConnectionState.Connected)
        {
            await connection.InvokeAsync("SyncCommand", payload.WorkspaceId, payload.RepositoryId, version, branch, outgoing, incoming, hasUpstream, cancellationToken);
            logger.LogInformation("MergeHookSync sent: workspace={WorkspaceId}, repo={RepoId}, version={Version}, branch={Branch}, ↑{Outgoing} ↓{Incoming}, hasUpstream={HasUpstream}",
                payload.WorkspaceId, payload.RepositoryId, version, branch, outgoing, incoming, hasUpstream);
        }
        else
        {
            logger.LogWarning("Hub not connected, cannot send MergeHookSync SyncCommand");
        }
    }
}
