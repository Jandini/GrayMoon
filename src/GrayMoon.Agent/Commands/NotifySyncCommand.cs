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

        var connection = hubProvider.Connection;
        if (connection?.State == HubConnectionState.Connected)
        {
            await connection.InvokeAsync("SyncCommand", payload.WorkspaceId, payload.RepositoryId, version, branch, cancellationToken);
            logger.LogInformation("SyncCommand sent: workspace={WorkspaceId}, repo={RepoId}, version={Version}, branch={Branch}",
                payload.WorkspaceId, payload.RepositoryId, version, branch);
        }
        else
        {
            logger.LogWarning("Hub not connected, cannot send SyncCommand");
        }
    }
}
