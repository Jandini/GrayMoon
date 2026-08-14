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
public sealed class CheckoutHookSyncCommand(IGitService git, IRepositoryStateProbe stateProbe, IAgentTokenProvider tokenProvider, IHubConnectionProvider hubProvider, ILogger<CheckoutHookSyncCommand> logger)
{
    public async Task ExecuteAsync(INotifyJob payload, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(payload.RepositoryPath))
        {
            logger.LogWarning("CheckoutHookSync job missing repositoryPath");
            return;
        }

        // Resolve default origin ref once so minimal fetch and the probe's commit-count calls share it.
        var defaultRef = await git.GetDefaultBranchOriginRefAsync(payload.RepositoryPath, cancellationToken);

        // Resolve the current branch cheaply (before running GitVersion) so the minimal fetch below can
        // target this branch's own configured upstream, not just the default branch. Using a placeholder
        // here would make FetchMinimalAsync's upstream lookup miss (no branch named "-"), silently skipping
        // the fetch of this branch's own remote-tracking ref and leaving it stale for the HasUpstream check below.
        var currentBranchForFetch = await git.GetCurrentBranchNameAsync(payload.RepositoryPath, cancellationToken) ?? "-";

        // Minimal fetch: only current branch and default branch, not all branches/tags.
        string? token = await tokenProvider.GetTokenForRepositoryAsync(payload.RepositoryId, cancellationToken);
        string? fetchError = null;
        if (token == null)
        {
            logger.LogDebug("CheckoutHookSync: no token available for repo {RepositoryId}; skipping minimal fetch.", payload.RepositoryId);
        }
        else
        {
            // Use minimal fetch before running GitVersion; GitVersion is invoked with /nofetch.
            var (fetchSuccess, err) = await git.FetchMinimalAsync(payload.RepositoryPath, currentBranchForFetch, defaultRef, token, cancellationToken);
            if (!fetchSuccess)
                fetchError = err;
        }

        // One probe for version, branch/tag, commit counts, upstream and projects. HasUpstream comes from
        // the branch's actual git-configured upstream (refreshed by the fetch above), not from whether a
        // local remote-tracking ref happens to already exist - that local-ref check could report "no
        // upstream" for a branch that does have one configured but whose ref this clone hadn't fetched yet,
        // resetting a correct BranchHasUpstream=true (e.g. set right after a push) back to false.
        var (state, _) = await stateProbe.CaptureAsync(payload.RepositoryPath, new RepositoryStateProbeOptions
        {
            IncludeGitVersion = true,
            GitVersionNonNormalize = true,
            IncludeProjects = true,
            // Remote branches let the app prune deleted ones; the full branch/tag lists are the Sync flow's job.
            IncludeRemoteBranchesOnly = true,
            DefaultBranchOriginRef = defaultRef,
            ErrorMessage = fetchError
        }, cancellationToken);

        // When on a tag, fetch remote tags so the app can compare and show an "upgrade" badge.
        IReadOnlyList<string>? remoteTags = null;
        if (state.CheckedOutTag != null && token != null)
        {
            var (fetchTagsSuccess, fetchTagsError) = await git.FetchTagsAsync(payload.RepositoryPath, token, cancellationToken);
            if (!fetchTagsSuccess)
                logger.LogWarning("CheckoutHookSync: tag fetch failed for repo {RepositoryId}: {Error}", payload.RepositoryId, fetchTagsError);
            remoteTags = await git.GetTagsAsync(payload.RepositoryPath, cancellationToken);
        }

        var version = state.GitVersion ?? "-";
        var branch = state.BranchName ?? "-";
        var remoteBranches = fetchError == null ? state.RemoteBranches : null;

        var connection = hubProvider.Connection;
        if (connection?.State == HubConnectionState.Connected)
        {
            var notification = new RepositorySyncNotification
            {
                WorkspaceId = payload.WorkspaceId,
                RepositoryId = payload.RepositoryId,
                Version = version,
                Branch = branch,
                Tag = state.CheckedOutTag,
                OutgoingCommits = state.OutgoingCommits,
                IncomingCommits = state.IncomingCommits,
                HasUpstream = state.HasUpstream,
                DefaultBranchBehind = state.DefaultBranchBehind,
                DefaultBranchAhead = state.DefaultBranchAhead,
                Projects = state.Projects,
                ErrorMessage = fetchError,
                // Include remote branches when fetch succeeded so the app can prune deleted remote branches from the DB
                RemoteBranches = remoteBranches,
                // Include tag list when on a tag so the app can persist tags and compute HasNewerTag
                RemoteTags = remoteTags?.ToList(),
                State = state
            };
            await connection.InvokeAsync(AgentHubMethods.SyncCommand, notification, cancellationToken);
            logger.LogInformation("CheckoutHookSync sent: workspace={WorkspaceId}, repo={RepoId}, version={Version}, branch={Branch}, ↑{Outgoing} ↓{Incoming}, hasUpstream={HasUpstream}",
                payload.WorkspaceId, payload.RepositoryId, version, branch, state.OutgoingCommits, state.IncomingCommits, state.HasUpstream);
        }
        else
        {
            logger.LogWarning("Hub not connected, cannot send CheckoutHookSync SyncCommand");
        }
    }
}
