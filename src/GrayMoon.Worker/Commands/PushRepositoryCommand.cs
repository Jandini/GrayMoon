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
    IGitService git,
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

        var workspacePath = git.GetWorkspacePath(request.WorkspaceRoot!, workspaceName);
        var repoPath = Path.Combine(workspacePath, repositoryName);

        if (!git.DirectoryExists(repoPath))
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
                await SendPostOperationSyncIfRequestedAsync(request, repoPath, integrate.Branch);

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
            await SendPostOperationSyncIfRequestedAsync(request, repoPath, branch);
            return new PushRepositoryResponse { Success = true };
        }

        var setTracking = !hasUpstream;
        var (pushSuccess, errorMessage) = await git.PushAsync(repoPath, branch, bearerToken, setTracking: setTracking, ct: cancellationToken);

        await SendPostOperationSyncIfRequestedAsync(request, repoPath, branch);

        return new PushRepositoryResponse
        {
            Success = pushSuccess,
            ErrorMessage = pushSuccess ? null : errorMessage
        };
    }

    private Task SendPostOperationSyncIfRequestedAsync(PushRepositoryRequest request, string repoPath, string branch)
    {
        if (request.RefreshVersionAfterPush)
            return SendPostOperationSyncAsync(request, repoPath, branch, versionOnly: true);

        _ = SendPostOperationSyncAsync(request, repoPath, branch, versionOnly: false);
        return Task.CompletedTask;
    }

    private async Task SendPostOperationSyncAsync(PushRepositoryRequest request, string repoPath, string branch, bool versionOnly)
    {
        try
        {
            var connection = hubProvider.Connection;
            if (connection?.State != HubConnectionState.Connected) return;

            var notification = await BuildPostOperationNotificationAsync(request, repoPath, branch, versionOnly);
            await connection.InvokeAsync(WorkerHubMethods.SyncCommand, notification, CancellationToken.None);
            logger.LogInformation("Post-push SyncCommand sent: workspace={WorkspaceId}, repo={RepoId}, outgoing={Outgoing}, versionOnly={VersionOnly}",
                request.WorkspaceId, request.RepositoryId, notification.OutgoingCommits, versionOnly);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Post-push SyncCommand failed for repo {RepoId}", request.RepositoryId);
        }
    }

    internal async Task<RepositorySyncNotification> BuildPostOperationNotificationAsync(PushRepositoryRequest request, string repoPath, string branch, bool versionOnly)
    {
        var capabilities = request.EffectiveCapabilities;
        var defaultRef = await git.GetDefaultBranchOriginRefAsync(repoPath, CancellationToken.None);
        int? outgoing = null;
        int? incoming = null;
        bool? hasUpstream = null;
        int? defaultBehind = null;
        int? defaultAhead = null;
        if (!versionOnly)
        {
            var divergenceRef = git.ToOriginBranchRef(await git.GetDivergenceBaseBranchAsync(repoPath, CancellationToken.None))
                ?? defaultRef;
            (outgoing, incoming, hasUpstream) = await git.GetCommitCountsAsync(repoPath, branch, defaultRef, CancellationToken.None);
            (defaultBehind, defaultAhead, _) = await git.GetCommitCountsVsDefaultAsync(repoPath, divergenceRef, CancellationToken.None);
        }

        var versionResult = await versionProviderFactory
            .Create(capabilities)
            .GetVersionAsync(repoPath, new RepositoryVersionOptions { NonNormalize = true }, CancellationToken.None);
        var versionBranch = versionResult.Result?.BranchName ?? versionResult.Result?.EscapedBranchName ?? branch;
        List<RepositorySyncProjectNotification>? syncProjects = null;
        var projectsProbed = capabilities.ShouldDiscoverProjects;
        if (projectsProbed)
        {
            var projects = await csProjFileService.FindAsync(repoPath, CancellationToken.None);
            syncProjects = RepositorySyncProjectMapper.ToNotifications(projects);
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
