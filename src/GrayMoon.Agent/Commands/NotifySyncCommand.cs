using GrayMoon.Agent.Abstractions;
using GrayMoon.Agent.Hub;
using GrayMoon.Agent.Services;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;

namespace GrayMoon.Agent.Commands;

public sealed class NotifySyncCommand(IGitService git, IHubConnectionProvider hubProvider, ILogger<NotifySyncCommand> logger) : INotifySyncHandler
{
    public async Task ExecuteAsync(INotifyJob payload, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(payload.RepositoryPath))
        {
            logger.LogWarning("NotifySync job missing repositoryPath");
            return;
        }

        var versionResult = await git.GetVersionAsync(payload.RepositoryPath, cancellationToken);
        var version = versionResult?.SemVer ?? versionResult?.FullSemVer ?? "-";
        var branch = versionResult?.BranchName ?? versionResult?.EscapedBranchName ?? "-";

        await git.FetchAsync(payload.RepositoryPath, includeTags: true, bearerToken: null, cancellationToken);
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
            logger.LogInformation("SyncCommand sent: workspace={WorkspaceId}, repo={RepoId}, version={Version}, branch={Branch}, ↑{Outgoing} ↓{Incoming}",
                payload.WorkspaceId, payload.RepositoryId, version, branch, outgoing, incoming);
        }
        else
        {
            logger.LogWarning("Hub not connected, cannot send SyncCommand");
        }
    }
}
