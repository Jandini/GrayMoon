using GrayMoon.Abstractions.Agent;
using GrayMoon.Abstractions.Notifications;
using GrayMoon.Agent.Abstractions;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;

namespace GrayMoon.Agent.Commands;

/// <summary>
/// Handles pre-push hooks: performs a minimal git fetch for the current branch and default origin
/// branch so commit counts use up-to-date remote tracking refs, then re-runs GitVersion and gets
/// commit counts and upstream/default status.
/// </summary>
public sealed class PushHookSyncCommand(IGitService git, IAgentTokenProvider tokenProvider, IHubConnectionProvider hubProvider, ILogger<PushHookSyncCommand> logger)
{
    public async Task ExecuteAsync(INotifyJob payload, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(payload.RepositoryPath))
        {
            logger.LogWarning("PushHookSync job missing repositoryPath");
            return;
        }

        // Resolve default origin ref once so minimal fetch and commit-count calls share it.
        var defaultRef = await git.GetDefaultBranchOriginRefAsync(payload.RepositoryPath, cancellationToken);

        string? fetchError = null;
        string? token = await tokenProvider.GetTokenForRepositoryAsync(payload.RepositoryId, cancellationToken);
        if (token == null)
        {
            logger.LogDebug("PushHookSync: no token available for repo {RepositoryId}; skipping minimal fetch.", payload.RepositoryId);
        }
        else
        {
            var (fetchSuccess, err) = await git.FetchMinimalAsync(payload.RepositoryPath, "-", defaultRef, token, cancellationToken);
            if (!fetchSuccess)
                fetchError = err;
        }

        var (versionResult, _) = await git.GetVersionAsync(payload.RepositoryPath, cancellationToken);
        var version = versionResult?.InformationalVersion ?? "-";
        var branch = versionResult?.BranchName ?? versionResult?.EscapedBranchName ?? "-";

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
            if (token != null)
            {
                var remoteBranches = await git.GetRemoteBranchesAsync(payload.RepositoryPath, token, cancellationToken);
                hasUpstream = remoteBranches.Any(r => string.Equals(r, branch, StringComparison.OrdinalIgnoreCase));
            }
            else
            {
                logger.LogDebug("PushHookSync: no token available for repo {RepositoryId}; skipping remote branch query.", payload.RepositoryId);
            }
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
            logger.LogInformation("PushHookSync sent: workspace={WorkspaceId}, repo={RepoId}, version={Version}, branch={Branch}, ↑{Outgoing} ↓{Incoming}, hasUpstream={HasUpstream}",
                payload.WorkspaceId, payload.RepositoryId, version, branch, outgoing, incoming, hasUpstream);
        }
        else
        {
            logger.LogWarning("Hub not connected, cannot send PushHookSync SyncCommand");
        }
    }
}
