using GrayMoon.Abstractions.Agent;
using GrayMoon.Abstractions.Notifications;
using GrayMoon.Agent.Abstractions;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;

namespace GrayMoon.Agent.Commands;

/// <summary>
/// Handles pre-push hooks: sends an immediate Version+Branch notification, then polls until
/// the push completes (outgoing commits drop to 0) and sends a final SyncCommand with the
/// actual post-push counts so DB persistence is updated regardless of whether the push
/// originated from GrayMoon or an external IDE.
/// </summary>
public sealed class PushHookSyncCommand(IGitService git, IHubConnectionProvider hubProvider, ILogger<PushHookSyncCommand> logger)
{
    public async Task ExecuteAsync(INotifyJob payload, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(payload.RepositoryPath))
        {
            logger.LogWarning("PushHookSync job missing repositoryPath");
            return;
        }

        var (versionResult, _) = await git.GetVersionAsync(payload.RepositoryPath, cancellationToken);
        var version = versionResult?.InformationalVersion ?? "-";
        var branch = versionResult?.BranchName ?? versionResult?.EscapedBranchName ?? "-";

        var currentTag = await git.GetCheckedOutTagAsync(payload.RepositoryPath, cancellationToken);
        if (currentTag != null)
            branch = "-";

        // The ref name as git knows it, which is what the deferred pass counts against and compares HEAD to.
        // GitVersion's branch name can be escaped (slashes replaced), so it is not usable as a ref.
        var pushedBranch = currentTag == null
            ? await git.GetCurrentBranchNameAsync(payload.RepositoryPath, cancellationToken)
            : null;

        var connection = hubProvider.Connection;
        if (connection?.State == HubConnectionState.Connected)
        {
            // Send an immediate notification with Version+Branch so the UI updates right away.
            // Commit counts are intentionally omitted here - this hook fires BEFORE the push
            // data is transferred, so any counts read now are stale.
            // SyncCommandHandler's HasValue guards ensure null fields do not overwrite DB values.
            var notification = new RepositorySyncNotification
            {
                WorkspaceId = payload.WorkspaceId,
                RepositoryId = payload.RepositoryId,
                Version = version,
                Branch = branch,
                Tag = currentTag,
                ErrorMessage = null
            };
            await connection.InvokeAsync(AgentHubMethods.SyncCommand, notification, cancellationToken);
            logger.LogInformation("PushHookSync initial sent: workspace={WorkspaceId}, repo={RepoId}, version={Version}, branch={Branch}",
                payload.WorkspaceId, payload.RepositoryId, version, branch);
        }
        else
        {
            logger.LogWarning("Hub not connected, cannot send PushHookSync SyncCommand");
        }

        // Fire-and-forget: poll until push completes, then send final SyncCommand with real counts.
        // Uses CancellationToken.None so it outlives the job's own token. A detached HEAD has no branch to
        // count against, so there is nothing to defer.
        if (!string.IsNullOrWhiteSpace(pushedBranch))
            _ = SendDeferredPostPushCountsAsync(payload, pushedBranch!);
    }

    /// <summary>
    /// Polls commit counts every 2 seconds (up to 30 seconds) waiting for outgoing commits to
    /// reach 0 after the push completes, then sends a SyncCommand so the app updates persistence.
    /// </summary>
    private async Task SendDeferredPostPushCountsAsync(INotifyJob payload, string branch)
    {
        const int maxChecks = 15;
        var repoPath = payload.RepositoryPath!;

        for (var attempt = 0; attempt < maxChecks; attempt++)
        {
            await Task.Delay(TimeSpan.FromSeconds(2), CancellationToken.None);

            try
            {
                var connection = hubProvider.Connection;
                if (connection?.State != HubConnectionState.Connected)
                {
                    logger.LogDebug("PushHookSync deferred: hub disconnected at attempt {Attempt}, aborting", attempt + 1);
                    return;
                }

                if (!await StillOnPushedBranchAsync(repoPath, branch, payload.RepositoryId))
                    return;

                var defaultRef = await git.GetDefaultBranchOriginRefAsync(repoPath, CancellationToken.None);
                var (outgoing, incoming, hasUpstream) = await git.GetCommitCountsAsync(repoPath, branch, defaultRef, CancellationToken.None);

                // Keep polling while push is still in progress (outgoing > 0) unless this is the last attempt
                if (outgoing > 0 && attempt < maxChecks - 1)
                {
                    logger.LogDebug("PushHookSync deferred: outgoing={Outgoing} at attempt {Attempt}, retrying", outgoing, attempt + 1);
                    continue;
                }

                // Push done (outgoing == 0 or null) or max attempts reached - send final notification
                var (defaultBehind, defaultAhead, _) = await git.GetCommitCountsVsDefaultAsync(repoPath, defaultRef, CancellationToken.None);
                var (versionResult, _) = await git.GetVersionAsync(repoPath, CancellationToken.None);
                var finalVersion = versionResult?.InformationalVersion ?? "-";
                var finalBranch = versionResult?.BranchName ?? versionResult?.EscapedBranchName ?? branch;

                var finalTag = await git.GetCheckedOutTagAsync(repoPath, CancellationToken.None);
                if (finalTag != null)
                    finalBranch = "-";

                // GitVersion alone can take seconds, so re-check rather than trusting the check above.
                if (!await StillOnPushedBranchAsync(repoPath, branch, payload.RepositoryId))
                    return;

                var finalNotification = new RepositorySyncNotification
                {
                    WorkspaceId = payload.WorkspaceId,
                    RepositoryId = payload.RepositoryId,
                    Version = finalVersion,
                    Branch = finalBranch,
                    Tag = finalTag,
                    OutgoingCommits = outgoing,
                    IncomingCommits = incoming,
                    HasUpstream = hasUpstream,
                    DefaultBranchBehind = defaultBehind,
                    DefaultBranchAhead = defaultAhead,
                };
                await connection.InvokeAsync(AgentHubMethods.SyncCommand, finalNotification, CancellationToken.None);
                logger.LogInformation("PushHookSync deferred SyncCommand sent: workspace={WorkspaceId}, repo={RepoId}, outgoing={Outgoing}, attempt={Attempt}",
                    payload.WorkspaceId, payload.RepositoryId, outgoing, attempt + 1);
                return;
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "PushHookSync deferred check failed at attempt {Attempt}", attempt + 1);
            }
        }

        logger.LogWarning("PushHookSync deferred: gave up after {MaxChecks} attempts for workspace={WorkspaceId}, repo={RepoId}",
            maxChecks, payload.WorkspaceId, payload.RepositoryId);
    }

    /// <summary>
    /// True while HEAD is still on the branch that was pushed. The deferred notification pairs counts taken
    /// for that branch with a branch name read at send time, so once HEAD has moved - a checkout, or a
    /// sync-to-default that deleted this very branch - the two disagree and sending would stamp one branch's
    /// upstream flag and counts onto another. Whatever moved HEAD reports its own state, so this pass bails.
    /// </summary>
    private async Task<bool> StillOnPushedBranchAsync(string repoPath, string pushedBranch, int repositoryId)
    {
        var currentBranch = await git.GetCurrentBranchNameAsync(repoPath, CancellationToken.None);
        if (string.Equals(currentBranch, pushedBranch, StringComparison.Ordinal))
            return true;

        logger.LogInformation("PushHookSync deferred: HEAD moved from {PushedBranch} to {CurrentBranch} for repo {RepoId}, dropping notification",
            pushedBranch, currentBranch ?? "<detached>", repositoryId);
        return false;
    }
}

