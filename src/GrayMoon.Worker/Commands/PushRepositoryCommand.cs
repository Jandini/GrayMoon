using GrayMoon.Abstractions.Worker;
using GrayMoon.Abstractions.Notifications;
using GrayMoon.Worker.Abstractions;
using GrayMoon.Worker.Jobs.Requests;
using GrayMoon.Worker.Jobs.Response;
using GrayMoon.Worker.Services;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;

namespace GrayMoon.Worker.Commands;

/// <summary>Fetches and pulls remote changes, then pushes when outgoing commits exist or upstream is not set.</summary>
public sealed class PushRepositoryCommand(
    IGitService git, IGitRepositoryReader reader,
    ICsProjFileService csProjFileService,
    IRepositoryVersionProviderFactory versionProviderFactory,
    GitRemoteIntegrateService remoteIntegrate,
    IHubConnectionProvider hubProvider,
    ILogger<PushRepositoryCommand> logger) : ICommandHandler<PushRepositoryRequest, PushRepositoryResponse>
{
    public async Task<PushRepositoryResponse> ExecuteAsync(PushRepositoryRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            return await ExecuteCoreAsync(request, cancellationToken);
        }
        catch (Exception ex)
        {
            return new PushRepositoryResponse
            {
                Success = false,
                ErrorMessage = ex.Message
            };
        }
    }

    private async Task<PushRepositoryResponse> ExecuteCoreAsync(PushRepositoryRequest request, CancellationToken cancellationToken = default)
    {
        var workspaceName = request.WorkspaceName ?? throw new ArgumentException("workspaceName required");
        var repositoryName = request.RepositoryName ?? throw new ArgumentException("repositoryName required");
        var bearerToken = request.BearerToken;

        var workspacePath = WorkerRepositoryPaths.GetWorkspacePath(request.WorkspaceRoot!, workspaceName);
        var repoPath = WorkerRepositoryPaths.Resolve(workspacePath, repositoryName, request.WorkspaceRepositoryName);

        if (!Directory.Exists(repoPath))
        {
            return new PushRepositoryResponse
            {
                Success = false,
                ErrorMessage = "Repository not found"
            };
        }

        var integrate = await remoteIntegrate.IntegrateAsync(repoPath, bearerToken, cancellationToken);
        if (!integrate.Success)
        {
            if (integrate.Branch != null)
                await SendPostOperationSyncAsync(request, repoPath, integrate.Branch, cancellationToken);

            return new PushRepositoryResponse
            {
                Success = false,
                ErrorMessage = integrate.ErrorMessage
            };
        }

        var branch = request.BranchName?.Trim();
        if (string.IsNullOrEmpty(branch))
            branch = integrate.Branch;
        if (string.IsNullOrWhiteSpace(branch))
        {
            return new PushRepositoryResponse
            {
                Success = false,
                ErrorMessage = "Could not determine branch name"
            };
        }

        var outgoing = integrate.Outgoing ?? 0;
        var hasUpstream = integrate.HasUpstream;

        if (outgoing <= 0 && hasUpstream)
        {
            await SendPostOperationSyncAsync(request, repoPath, branch, cancellationToken);
            return new PushRepositoryResponse { Success = true };
        }

        var setTracking = !hasUpstream;
        var (pushSuccess, errorMessage) = await git.PushAsync(repoPath, branch, bearerToken, setTracking: setTracking, ct: cancellationToken);

        await SendPostOperationSyncAsync(request, repoPath, branch, cancellationToken);

        return new PushRepositoryResponse
        {
            Success = pushSuccess,
            ErrorMessage = pushSuccess ? null : errorMessage
        };
    }

    /// <summary>Test seam: replaces the hub send (and the connected check) for the post-push sync.</summary>
    internal Func<RepositorySyncNotification, CancellationToken, Task>? SendSyncOverride { get; init; }

    /// <summary>
    /// Sends the post-push sync before the command returns, so the scan runs inside this job's pool slot, counts as
    /// pending work, and ends with the job's cancellation. A plain push sends the full state; with
    /// <c>RefreshVersionAfterPush</c> only the version is recomputed.
    /// </summary>
    private async Task SendPostOperationSyncAsync(PushRepositoryRequest request, string repoPath, string branch, CancellationToken cancellationToken)
    {
        var versionOnly = request.RefreshVersionAfterPush;
        try
        {
            var connection = hubProvider.Connection;
            if (SendSyncOverride == null && connection?.State != HubConnectionState.Connected) return;

            var notification = await BuildPostOperationNotificationAsync(request, repoPath, branch, versionOnly, cancellationToken);
            if (SendSyncOverride != null)
                await SendSyncOverride(notification, cancellationToken);
            else
                await connection!.InvokeAsync(WorkerHubMethods.SyncCommand, notification, cancellationToken);
            logger.LogInformation("Post-push SyncCommand sent: workspace={WorkspaceId}, repo={RepoId}, outgoing={Outgoing}, versionOnly={VersionOnly}",
                request.WorkspaceId, request.RepositoryId, notification.OutgoingCommits, versionOnly);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The push itself already has its outcome; only the follow-up scan was cancelled.
            logger.LogDebug("Post-push SyncCommand cancelled for repo {RepoId}", request.RepositoryId);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Post-push SyncCommand failed for repo {RepoId}", request.RepositoryId);
        }
    }

    internal async Task<RepositorySyncNotification> BuildPostOperationNotificationAsync(PushRepositoryRequest request, string repoPath, string branch, bool versionOnly, CancellationToken cancellationToken = default)
    {
        var capabilities = request.EffectiveCapabilities;
        var defaultRef = await reader.GetDefaultBranchOriginRefAsync(repoPath, cancellationToken);
        int? outgoing = null;
        int? incoming = null;
        bool? hasUpstream = null;
        int? defaultBehind = null;
        int? defaultAhead = null;
        if (!versionOnly)
        {
            var divergenceRef = OriginDefaultRef.ToOriginBranchRef(await reader.GetDivergenceBaseBranchAsync(repoPath, cancellationToken))
                ?? defaultRef;
            (outgoing, incoming, hasUpstream) = await reader.GetCommitCountsAsync(repoPath, branch, defaultRef, cancellationToken);
            (defaultBehind, defaultAhead, _) = await reader.GetCommitCountsVsDefaultAsync(repoPath, divergenceRef, cancellationToken);
        }

        var versionResult = await versionProviderFactory
            .Create(capabilities)
            .GetVersionAsync(repoPath, new RepositoryVersionOptions { NonNormalize = true }, cancellationToken);
        var versionBranch = versionResult.Result?.BranchName ?? versionResult.Result?.EscapedBranchName ?? branch;
        List<RepositorySyncProjectNotification>? syncProjects = null;
        var projectsProbed = request.EffectiveCapabilities.ShouldDiscoverProjects;
        if (projectsProbed)
        {
            try
            {
                var projects = await csProjFileService.FindAsync(repoPath, cancellationToken);
                syncProjects = RepositorySyncProjectMapper.ToNotifications(projects);
            }
            catch (ProjectDiscoveryException ex)
            {
                // The push already succeeded. Report projects as not probed so persisted rows survive.
                logger.LogWarning(ex, "Post-push project discovery failed for repo {RepoId}; projects left unprobed", request.RepositoryId);
                projectsProbed = false;
            }
        }

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
            Projects = syncProjects,
            // A version-only pass deliberately skips the counts, and a skipped enrichment step reports
            // itself as not probed, so the persisted values survive.
            State = new RepositoryStateSnapshot
            {
                BranchName = versionBranch,
                GitVersion = versionResult.InformationalVersion,
                OutgoingCommits = outgoing,
                IncomingCommits = incoming,
                DefaultBranchBehind = defaultBehind,
                DefaultBranchAhead = defaultAhead,
                HasUpstream = hasUpstream,
                Projects = syncProjects,
                IdentityProbed = true,
                GitVersionProbed = versionResult.Probed,
                CommitCountsProbed = outgoing.HasValue || incoming.HasValue,
                UpstreamProbed = hasUpstream.HasValue,
                ProjectsProbed = projectsProbed,
            }
        };
    }
}
