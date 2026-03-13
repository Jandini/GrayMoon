using GrayMoon.Abstractions.Agent;
using GrayMoon.Abstractions.Notifications;
using GrayMoon.Agent.Abstractions;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;

namespace GrayMoon.Agent.Commands;

/// <summary>
/// Handles post-checkout hooks: runs GitVersion, then performs a minimal git fetch for the current
/// branch and default origin branch before computing commit counts. This keeps commit counts and
/// upstream/default comparisons correct without paying the cost of a full fetch of all branches
/// and tags (full fetch is done by Sync and branch list flows).
/// </summary>
public sealed class CheckoutHookSyncCommand(IGitService git, IAgentTokenProvider tokenProvider, IHubConnectionProvider hubProvider, ILogger<CheckoutHookSyncCommand> logger)
{
    public async Task ExecuteAsync(INotifyJob payload, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(payload.RepositoryPath))
        {
            logger.LogWarning("CheckoutHookSync job missing repositoryPath");
            return;
        }

        var (versionResult, _) = await git.GetVersionAsync(payload.RepositoryPath, cancellationToken);
        var version = versionResult?.InformationalVersion ?? "-";
        var branch = versionResult?.BranchName ?? versionResult?.EscapedBranchName ?? "-";

        // Resolve default origin ref once so minimal fetch and commit-count calls share it.
        var defaultRef = await git.GetDefaultBranchOriginRefAsync(payload.RepositoryPath, cancellationToken);

        // Minimal fetch: only current branch and default branch, not all branches/tags.
        string? token = await tokenProvider.GetTokenForRepositoryAsync(payload.RepositoryId, cancellationToken);
        string? fetchError = null;
        if (token == null)
        {
            logger.LogDebug("CheckoutHookSync: no token available for repo {RepositoryId}; skipping minimal fetch.", payload.RepositoryId);
        }
        else
        {
            var (fetchSuccess, err) = await git.FetchMinimalAsync(payload.RepositoryPath, branch, defaultRef, token, cancellationToken);
            if (!fetchSuccess)
                fetchError = err;
        }

        int? outgoing = null;
        int? incoming = null;
        int? defaultBehind = null;
        int? defaultAhead = null;
        if (branch != "-")
        {
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
