using GrayMoon.Abstractions.Agent;
using GrayMoon.Abstractions.Notifications;
using GrayMoon.Agent.Abstractions;
using GrayMoon.Agent.Jobs.Requests;
using GrayMoon.Agent.Jobs.Response;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;

namespace GrayMoon.Agent.Commands;

/// <summary>Pushes the current branch to origin and sets upstream (-u) so the branch is upstreamed even when there are no commits to push.</summary>
public sealed class PushRepositoryCommand(IGitService git, IHubConnectionProvider hubProvider, ILogger<PushRepositoryCommand> logger) : ICommandHandler<PushRepositoryRequest, PushRepositoryResponse>
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

        string? branch = request.BranchName?.Trim();
        if (string.IsNullOrEmpty(branch))
        {
            branch = await git.GetCurrentBranchNameAsync(repoPath, cancellationToken);
            if (string.IsNullOrWhiteSpace(branch))
            {
                return new PushRepositoryResponse
                {
                    Success = false,
                    ErrorMessage = "Could not determine branch name"
                };
            }
        }

        if (string.IsNullOrWhiteSpace(branch))
        {
            return new PushRepositoryResponse
            {
                Success = false,
                ErrorMessage = "Could not determine branch name"
            };
        }

        // Push with -u so the branch is upstreamed even when there are no commits to push
        var (success, errorMessage) = await git.PushAsync(repoPath, branch, bearerToken, setTracking: true, ct: cancellationToken);

        if (success)
        {
            // Fire-and-forget: send actual post-push commit counts to the app so DB is updated
            // immediately after push — for both GrayMoon-initiated and any downstream refresh path.
            _ = SendPostPushSyncAsync(request.WorkspaceId, request.RepositoryId, repoPath, branch);
        }

        return new PushRepositoryResponse
        {
            Success = success,
            ErrorMessage = success ? null : errorMessage
        };
    }

    private async Task SendPostPushSyncAsync(int workspaceId, int repositoryId, string repoPath, string branch)
    {
        try
        {
            var connection = hubProvider.Connection;
            if (connection?.State != HubConnectionState.Connected) return;

            var defaultRef = await git.GetDefaultBranchOriginRefAsync(repoPath, CancellationToken.None);
            var (outgoing, incoming, hasUpstream) = await git.GetCommitCountsAsync(repoPath, branch, defaultRef, CancellationToken.None);
            var (defaultBehind, defaultAhead, _) = await git.GetCommitCountsVsDefaultAsync(repoPath, defaultRef, CancellationToken.None);
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
            logger.LogInformation("Post-push SyncCommand sent: workspace={WorkspaceId}, repo={RepoId}, outgoing={Outgoing}",
                workspaceId, repositoryId, outgoing);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Post-push SyncCommand failed for repo {RepoId}", repositoryId);
        }
    }
}
