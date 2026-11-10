using GrayMoon.Abstractions.Worker;
using GrayMoon.Abstractions.Notifications;
using GrayMoon.Worker.Abstractions;
using GrayMoon.Worker.Jobs.Requests;
using GrayMoon.Worker.Jobs.Response;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;
using GrayMoon.Worker.Services;

namespace GrayMoon.Worker.Commands;

/// <summary>Resets the current branch to origin/branch (mixed or hard) to undo local outgoing commits.</summary>
public sealed class UndoPushCommand(
    IGitService git, IGitRepositoryReader reader,
    IRepositoryVersionProviderFactory versionProviderFactory,
    IHubConnectionProvider hubProvider,
    ILogger<UndoPushCommand> logger) : ICommandHandler<UndoPushRequest, UndoPushResponse>
{
    public async Task<UndoPushResponse> ExecuteAsync(UndoPushRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            return await ExecuteCoreAsync(request, cancellationToken);
        }
        catch (Exception ex)
        {
            return new UndoPushResponse { Success = false, ErrorMessage = ex.Message };
        }
    }

    private async Task<UndoPushResponse> ExecuteCoreAsync(UndoPushRequest request, CancellationToken cancellationToken)
    {
        var workspaceName = request.WorkspaceName ?? throw new ArgumentException("workspaceName required");
        var repositoryName = request.RepositoryName ?? throw new ArgumentException("repositoryName required");

        var workspacePath = WorkerRepositoryPaths.GetWorkspacePath(request.WorkspaceRoot!, workspaceName);
        var repoPath = WorkerRepositoryPaths.Resolve(workspacePath, repositoryName, request.WorkspaceRepositoryName);

        if (!Directory.Exists(repoPath))
            return new UndoPushResponse { Success = false, ErrorMessage = "Repository not found" };

        var branch = request.BranchName?.Trim();
        if (string.IsNullOrEmpty(branch))
        {
            branch = await reader.GetCurrentBranchNameAsync(repoPath, cancellationToken);
            if (string.IsNullOrWhiteSpace(branch))
                return new UndoPushResponse { Success = false, ErrorMessage = "Could not determine branch name" };
        }

        var (success, errorMessage) = await git.ResetToRemoteAsync(repoPath, branch, request.KeepChanges, request.BearerToken, cancellationToken);

        if (success)
            await SendPostResetSyncAsync(request, repoPath, branch, cancellationToken);

        return new UndoPushResponse { Success = success, ErrorMessage = success ? null : errorMessage };
    }

    /// <summary>Test seam: replaces the hub send (and the connected check) for the post-reset sync.</summary>
    internal Func<RepositorySyncNotification, CancellationToken, Task>? SendSyncOverride { get; init; }

    /// <summary>
    /// Sends the post-reset sync before the command returns, so the scan runs inside this job's pool slot, counts as
    /// pending work, and ends with the job's cancellation.
    /// </summary>
    private async Task SendPostResetSyncAsync(UndoPushRequest request, string repoPath, string branch, CancellationToken cancellationToken)
    {
        try
        {
            var connection = hubProvider.Connection;
            if (SendSyncOverride == null && connection?.State != HubConnectionState.Connected) return;

            var notification = await BuildPostResetNotificationAsync(request, repoPath, branch, cancellationToken);
            if (SendSyncOverride != null)
                await SendSyncOverride(notification, cancellationToken);
            else
                await connection!.InvokeAsync(WorkerHubMethods.SyncCommand, notification, cancellationToken);
            logger.LogInformation("Post-reset SyncCommand sent: workspace={WorkspaceId}, repo={RepoId}, outgoing={Outgoing}",
                request.WorkspaceId, request.RepositoryId, notification.OutgoingCommits);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The reset itself already succeeded; only the follow-up scan was cancelled.
            logger.LogDebug("Post-reset SyncCommand cancelled for repo {RepoId}", request.RepositoryId);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Post-reset SyncCommand failed for repo {RepoId}", request.RepositoryId);
        }
    }

    internal async Task<RepositorySyncNotification> BuildPostResetNotificationAsync(UndoPushRequest request, string repoPath, string branch, CancellationToken cancellationToken = default)
    {
        var defaultRef = await reader.GetDefaultBranchOriginRefAsync(repoPath, cancellationToken);
        var divergenceRef = OriginDefaultRef.ToOriginBranchRef(await reader.GetDivergenceBaseBranchAsync(repoPath, cancellationToken))
            ?? defaultRef;
        var (outgoing, incoming, hasUpstream) = await reader.GetCommitCountsAsync(repoPath, branch, defaultRef, cancellationToken);
        var (defaultBehind, defaultAhead, _) = await reader.GetCommitCountsVsDefaultAsync(repoPath, divergenceRef, cancellationToken);
        var versionResult = await versionProviderFactory
            .Create(request.Capabilities)
            .GetVersionAsync(repoPath, new RepositoryVersionOptions { NonNormalize = true }, cancellationToken);
        var versionBranch = versionResult.Result?.BranchName ?? versionResult.Result?.EscapedBranchName ?? branch;

        return new RepositorySyncNotification
        {
            WorkspaceId = request.WorkspaceId,
            RepositoryId = request.RepositoryId,
            // Required for Feature attribution - null path is treated as special Workspace and
            // would mirror this worktree's branch onto the shared WorkspaceRepositoryLink.
            RepositoryPath = repoPath,
            Version = versionResult.VersionOrPlaceholder,
            Branch = versionBranch,
            OutgoingCommits = outgoing,
            IncomingCommits = incoming,
            HasUpstream = hasUpstream,
            DefaultBranchBehind = defaultBehind,
            DefaultBranchAhead = defaultAhead,
            // This pass never scans the working tree, so the project marker stays false and the
            // persisted project rows survive the reset.
            State = new RepositoryStateSnapshot
            {
                BranchName = versionBranch,
                GitVersion = versionResult.InformationalVersion,
                OutgoingCommits = outgoing,
                IncomingCommits = incoming,
                DefaultBranchBehind = defaultBehind,
                DefaultBranchAhead = defaultAhead,
                HasUpstream = hasUpstream,
                IdentityProbed = true,
                GitVersionProbed = versionResult.Probed,
                CommitCountsProbed = outgoing.HasValue || incoming.HasValue,
                UpstreamProbed = true,
            }
        };
    }
}
