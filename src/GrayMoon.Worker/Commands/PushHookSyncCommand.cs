using GrayMoon.Abstractions.Worker;
using GrayMoon.Abstractions.Notifications;
using GrayMoon.Abstractions.Workspaces;
using GrayMoon.Worker.Abstractions;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;

namespace GrayMoon.Worker.Commands;

/// <summary>
/// Handles pre-push hooks: sends an immediate Version+Branch notification, then polls until
/// the push completes (outgoing commits drop to 0) and sends a final SyncCommand with the
/// actual post-push counts so DB persistence is updated regardless of whether the push
/// originated from GrayMoon or an external IDE.
/// </summary>
public sealed class PushHookSyncCommand(
    IGitService git,
    IRepositoryStateProbe stateProbe,
    IWorkspaceCapabilityProvider capabilityProvider,
    IHubConnectionProvider hubProvider,
    ILogger<PushHookSyncCommand> logger)
{
    public async Task ExecuteAsync(INotifyJob payload, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(payload.RepositoryPath))
        {
            logger.LogWarning("PushHookSync job missing repositoryPath");
            return;
        }

        // There is no app request to carry capabilities on this path, so they are resolved here.
        var capabilities = await capabilityProvider.GetAsync(payload.WorkspaceId, cancellationToken);

        // Identity and version only. Commit counts are intentionally not probed here - this hook fires
        // BEFORE the push data is transferred, so any counts read now are stale - and the project scan
        // belongs to the deferred pass, which runs once rather than on every attempt.
        var (state, _) = await stateProbe.CaptureAsync(payload.RepositoryPath, new RepositoryStateProbeOptions
        {
            IncludeGitVersion = true,
            IncludeCommitCounts = false,
            Capabilities = capabilities
        }, cancellationToken);

        var version = state.GitVersion ?? "-";
        var branch = state.BranchName ?? "-";

        // The ref name as git knows it, which is what the deferred pass counts against and compares HEAD to.
        // The version provider's branch name can be escaped (slashes replaced), so it is not usable as a ref.
        var pushedBranch = state.CheckedOutTag == null
            ? await git.GetCurrentBranchNameAsync(payload.RepositoryPath, cancellationToken)
            : null;

        var connection = hubProvider.Connection;
        if (connection?.State == HubConnectionState.Connected)
        {
            // Send an immediate notification with Version+Branch so the UI updates right away.
            // SyncCommandHandler's probe markers ensure the groups this pass skipped are not overwritten.
            var notification = new RepositorySyncNotification
            {
                WorkspaceId = payload.WorkspaceId,
                RepositoryId = payload.RepositoryId,
                RepositoryPath = payload.RepositoryPath,
                Version = version,
                GitVersionFailed = state.GitVersionProbed && state.GitVersion == null,
                Branch = branch,
                Tag = state.CheckedOutTag,
                ErrorMessage = null,
                State = state
            };
            await connection.InvokeAsync(WorkerHubMethods.SyncCommand, notification, cancellationToken);
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
            _ = SendDeferredPostPushCountsAsync(payload, pushedBranch!, capabilities);
    }

    /// <summary>
    /// Polls commit counts every 2 seconds (up to 30 seconds) waiting for outgoing commits to
    /// reach 0 after the push completes, then sends a SyncCommand so the app updates persistence.
    /// </summary>
    private async Task SendDeferredPostPushCountsAsync(INotifyJob payload, string branch, RepositoryOperationCapabilities capabilities)
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

                // The wait itself stays a bare count: polling must not relaunch the version provider or the
                // project scan on every attempt.
                var defaultRef = await git.GetDefaultBranchOriginRefAsync(repoPath, CancellationToken.None);
                var (outgoing, _, _) = await git.GetCommitCountsAsync(repoPath, branch, defaultRef, CancellationToken.None);

                // Keep polling while push is still in progress (outgoing > 0) unless this is the last attempt
                if (outgoing > 0 && attempt < maxChecks - 1)
                {
                    logger.LogDebug("PushHookSync deferred: outgoing={Outgoing} at attempt {Attempt}, retrying", outgoing, attempt + 1);
                    continue;
                }

                // Push done (outgoing == 0 or null) or max attempts reached - capture the full state once.
                var (state, _) = await stateProbe.CaptureAsync(repoPath, new RepositoryStateProbeOptions
                {
                    IncludeGitVersion = true,
                    IncludeProjects = true,
                    DefaultBranchOriginRef = defaultRef,
                    BranchNameOverride = branch,
                    Capabilities = capabilities
                }, CancellationToken.None);

                // The version provider alone can take seconds, so re-check rather than trusting the check above.
                if (!await StillOnPushedBranchAsync(repoPath, branch, payload.RepositoryId))
                    return;

                var finalNotification = new RepositorySyncNotification
                {
                    WorkspaceId = payload.WorkspaceId,
                    RepositoryId = payload.RepositoryId,
                    RepositoryPath = payload.RepositoryPath,
                    Version = state.GitVersion ?? "-",
                    GitVersionFailed = state.GitVersionProbed && state.GitVersion == null,
                    Branch = state.BranchName ?? "-",
                    Tag = state.CheckedOutTag,
                    OutgoingCommits = state.OutgoingCommits,
                    IncomingCommits = state.IncomingCommits,
                    HasUpstream = state.HasUpstream,
                    DefaultBranchBehind = state.DefaultBranchBehind,
                    DefaultBranchAhead = state.DefaultBranchAhead,
                    Projects = state.Projects,
                    State = state
                };
                await connection.InvokeAsync(WorkerHubMethods.SyncCommand, finalNotification, CancellationToken.None);
                logger.LogInformation("PushHookSync deferred SyncCommand sent: workspace={WorkspaceId}, repo={RepoId}, outgoing={Outgoing}, attempt={Attempt}",
                    payload.WorkspaceId, payload.RepositoryId, state.OutgoingCommits, attempt + 1);
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
    /// return-to-default that deleted this very branch - the two disagree and sending would stamp one branch's
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
