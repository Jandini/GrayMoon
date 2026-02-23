using GrayMoon.Agent.Abstractions;
using GrayMoon.Agent.Hub;
using GrayMoon.Agent.Services;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;

namespace GrayMoon.Agent.Commands;

/// <summary>
/// Handles post-checkout hooks: runs GitVersion and git fetch in parallel, then gets commit counts.
/// Fetch is required here because a branch switch may reveal new remote commits.
/// </summary>
public sealed class CheckoutHookSyncCommand(IGitService git, IHubConnectionProvider hubProvider, ILogger<CheckoutHookSyncCommand> logger)
{
    public async Task ExecuteAsync(INotifyJob payload, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(payload.RepositoryPath))
        {
            logger.LogWarning("CheckoutHookSync job missing repositoryPath");
            return;
        }

        // GetVersionAsync (dotnet-gitversion) reads local history; FetchAsync writes refs/remotes/ atomically.
        // Both are safe to run in parallel. GetCommitCountsAsync reads origin/{branch} so must wait for fetch.
        var versionTask = git.GetVersionAsync(payload.RepositoryPath, cancellationToken);
        var fetchTask = git.FetchAsync(payload.RepositoryPath, includeTags: true, bearerToken: null, cancellationToken);

        var (versionResult, _) = await versionTask;
        var version = versionResult?.SemVer ?? versionResult?.FullSemVer ?? "-";
        var branch = versionResult?.BranchName ?? versionResult?.EscapedBranchName ?? "-";

        await fetchTask; // must complete before commit counts (needs up-to-date remote tracking refs)

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
            logger.LogInformation("CheckoutHookSync sent: workspace={WorkspaceId}, repo={RepoId}, version={Version}, branch={Branch}, ↑{Outgoing} ↓{Incoming}",
                payload.WorkspaceId, payload.RepositoryId, version, branch, outgoing, incoming);
        }
        else
        {
            logger.LogWarning("Hub not connected, cannot send CheckoutHookSync SyncCommand");
        }
    }
}
