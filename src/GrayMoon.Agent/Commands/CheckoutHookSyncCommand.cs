using GrayMoon.Agent.Abstractions;
using GrayMoon.Agent.Hub;
using GrayMoon.Agent.Services;
using GrayMoon.Abstractions.Agent;
using GrayMoon.Abstractions.Notifications;
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
        var version = versionResult?.InformationalVersion ?? "-";
        var branch = versionResult?.BranchName ?? versionResult?.EscapedBranchName ?? "-";

        var (fetchSuccess, fetchError) = await fetchTask; // must complete before commit counts (needs up-to-date remote tracking refs)

        int? outgoing = null;
        int? incoming = null;
        int? defaultBehind = null;
        int? defaultAhead = null;
        if (branch != "-")
        {
            var defaultRef = await git.GetDefaultBranchOriginRefAsync(payload.RepositoryPath, cancellationToken);
            var (o, i, _) = await git.GetCommitCountsAsync(payload.RepositoryPath, branch, defaultRef, cancellationToken);
            outgoing = o;
            incoming = i;
            var (db, da, _) = await git.GetCommitCountsVsDefaultAsync(payload.RepositoryPath, defaultRef, cancellationToken);
            defaultBehind = db;
            defaultAhead = da;
        }

        bool? hasUpstream = null;
        if (branch != "-")
        {
            var remoteBranches = await git.GetRemoteBranchesFromRefsAsync(payload.RepositoryPath, cancellationToken);
            hasUpstream = remoteBranches.Any(r => string.Equals(r, branch, StringComparison.OrdinalIgnoreCase));
        }

        var connection = hubProvider.Connection;
        if (connection?.State == HubConnectionState.Connected)
        {
            var notification = new RepositorySyncNotification
            {
                WorkspaceId = payload.WorkspaceId,
                RepositoryId = payload.RepositoryId,
                Version = version,
                Branch = branch,
                OutgoingCommits = outgoing,
                IncomingCommits = incoming,
                HasUpstream = hasUpstream,
                DefaultBranchBehind = defaultBehind,
                DefaultBranchAhead = defaultAhead,
                ErrorMessage = fetchError
            };
            await connection.InvokeAsync(AgentHubMethods.SyncCommand, notification, cancellationToken);
            logger.LogInformation("CheckoutHookSync sent: workspace={WorkspaceId}, repo={RepoId}, version={Version}, branch={Branch}, ↑{Outgoing} ↓{Incoming}, hasUpstream={HasUpstream}",
                payload.WorkspaceId, payload.RepositoryId, version, branch, outgoing, incoming, hasUpstream);
        }
        else
        {
            logger.LogWarning("Hub not connected, cannot send CheckoutHookSync SyncCommand");
        }
    }
}
