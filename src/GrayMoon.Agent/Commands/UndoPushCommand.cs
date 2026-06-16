using GrayMoon.Abstractions.Agent;
using GrayMoon.Abstractions.Notifications;
using GrayMoon.Agent.Abstractions;
using GrayMoon.Agent.Jobs.Requests;
using GrayMoon.Agent.Jobs.Response;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;

namespace GrayMoon.Agent.Commands;

/// <summary>Resets the current branch to origin/branch (mixed or hard) to undo local outgoing commits.</summary>
public sealed class UndoPushCommand(IGitService git, IHubConnectionProvider hubProvider, ILogger<UndoPushCommand> logger) : ICommandHandler<UndoPushRequest, UndoPushResponse>
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

        var workspacePath = git.GetWorkspacePath(request.WorkspaceRoot!, workspaceName);
        var repoPath = Path.Combine(workspacePath, repositoryName);

        if (!git.DirectoryExists(repoPath))
            return new UndoPushResponse { Success = false, ErrorMessage = "Repository not found" };

        var branch = request.BranchName?.Trim();
        if (string.IsNullOrEmpty(branch))
        {
            branch = await git.GetCurrentBranchNameAsync(repoPath, cancellationToken);
            if (string.IsNullOrWhiteSpace(branch))
                return new UndoPushResponse { Success = false, ErrorMessage = "Could not determine branch name" };
        }

        var (success, errorMessage) = await git.ResetToRemoteAsync(repoPath, branch, request.KeepChanges, cancellationToken);

        if (success)
            _ = SendPostResetSyncAsync(request.WorkspaceId, request.RepositoryId, repoPath, branch);

        return new UndoPushResponse { Success = success, ErrorMessage = success ? null : errorMessage };
    }

    private async Task SendPostResetSyncAsync(int workspaceId, int repositoryId, string repoPath, string branch)
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
            logger.LogInformation("Post-reset SyncCommand sent: workspace={WorkspaceId}, repo={RepoId}, outgoing={Outgoing}",
                workspaceId, repositoryId, outgoing);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Post-reset SyncCommand failed for repo {RepoId}", repositoryId);
        }
    }
}
