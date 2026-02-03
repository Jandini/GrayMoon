using GrayMoon.Agent.Hub;
using GrayMoon.Agent.Jobs;
using GrayMoon.Agent.Services;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;

namespace GrayMoon.Agent.Commands;

public sealed class NotifySyncHandler : INotifySyncHandler
{
    private readonly GitOperations _git;
    private readonly IHubConnectionProvider _hubProvider;
    private readonly ILogger<NotifySyncHandler> _logger;

    public NotifySyncHandler(GitOperations git, IHubConnectionProvider hubProvider, ILogger<NotifySyncHandler> logger)
    {
        _git = git;
        _hubProvider = hubProvider;
        _logger = logger;
    }

    public async Task ExecuteAsync(INotifyJob payload, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(payload.RepositoryPath))
        {
            _logger.LogWarning("NotifySync job missing repositoryPath");
            return;
        }

        var versionResult = await _git.GetVersionAsync(payload.RepositoryPath, cancellationToken);
        var version = versionResult?.SemVer ?? versionResult?.FullSemVer ?? "-";
        var branch = versionResult?.BranchName ?? versionResult?.EscapedBranchName ?? "-";

        var connection = _hubProvider.Connection;
        if (connection?.State == HubConnectionState.Connected)
        {
            await connection.InvokeAsync("SyncCommand", payload.WorkspaceId, payload.RepositoryId, version, branch, cancellationToken);
            _logger.LogInformation("SyncCommand sent: workspace={WorkspaceId}, repo={RepoId}, version={Version}, branch={Branch}",
                payload.WorkspaceId, payload.RepositoryId, version, branch);
        }
        else
        {
            _logger.LogWarning("Hub not connected, cannot send SyncCommand");
        }
    }
}
