using GrayMoon.Abstractions.Agent;
using GrayMoon.Abstractions.Notifications;
using GrayMoon.Agent.Abstractions;
using GrayMoon.Agent.Jobs.Requests;
using GrayMoon.Agent.Jobs.Response;
using GrayMoon.Agent.Services;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;

namespace GrayMoon.Agent.Commands;

/// <summary>Fetches and pulls remote changes, then pushes when outgoing commits exist or upstream is not set.</summary>
public sealed class PushRepositoryCommand(
    IGitService git,
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
            return SendPostOperationSyncAsync(request.WorkspaceId, request.RepositoryId, repoPath, branch, versionOnly: true);

        _ = SendPostOperationSyncAsync(request.WorkspaceId, request.RepositoryId, repoPath, branch, versionOnly: false);
        return Task.CompletedTask;
    }

    private async Task SendPostOperationSyncAsync(int workspaceId, int repositoryId, string repoPath, string branch, bool versionOnly)
    {
        try
        {
            var connection = hubProvider.Connection;
            if (connection?.State != HubConnectionState.Connected) return;

            var defaultRef = await git.GetDefaultBranchOriginRefAsync(repoPath, CancellationToken.None);
            int? outgoing = null;
            int? incoming = null;
            bool? hasUpstream = null;
            int? defaultBehind = null;
            int? defaultAhead = null;
            if (!versionOnly)
            {
                (outgoing, incoming, hasUpstream) = await git.GetCommitCountsAsync(repoPath, branch, defaultRef, CancellationToken.None);
                (defaultBehind, defaultAhead, _) = await git.GetCommitCountsVsDefaultAsync(repoPath, defaultRef, CancellationToken.None);
            }

            var (versionResult, _) = await git.GetVersionAsync(repoPath, nonNormalize: true, CancellationToken.None);
            var version = versionResult?.InformationalVersion ?? "-";
            var versionBranch = versionResult?.BranchName ?? versionResult?.EscapedBranchName ?? branch;

            var notification = new RepositorySyncNotification
            {
                WorkspaceId = workspaceId,
                RepositoryId = repositoryId,
                Version = version,
                Branch = versionBranch,
                OutgoingCommits = outgoing,
                IncomingCommits = incoming,
                HasUpstream = hasUpstream,
                DefaultBranchBehind = defaultBehind,
                DefaultBranchAhead = defaultAhead,
            };
            await connection.InvokeAsync(AgentHubMethods.SyncCommand, notification, CancellationToken.None);
            logger.LogInformation("Post-push SyncCommand sent: workspace={WorkspaceId}, repo={RepoId}, outgoing={Outgoing}, versionOnly={VersionOnly}",
                workspaceId, repositoryId, outgoing, versionOnly);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Post-push SyncCommand failed for repo {RepoId}", repositoryId);
        }
    }
}
