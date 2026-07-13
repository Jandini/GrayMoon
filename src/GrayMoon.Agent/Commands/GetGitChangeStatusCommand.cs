using GrayMoon.Agent.Abstractions;
using GrayMoon.Agent.Jobs.Requests;
using GrayMoon.Agent.Jobs.Response;
using GrayMoon.Agent.Services.GitChanges;

namespace GrayMoon.Agent.Commands;

/// <summary>
/// Runs (or coalesces into an already-running) authoritative git status scan for one repository, then
/// keeps a watcher lease alive for a while so background monitoring continues to track this repository
/// even after this specific request completes - a subsequent status request renews the lease.
/// </summary>
public sealed class GetGitChangeStatusCommand(
    IGitService git,
    GitStatusRefreshCoordinator coordinator,
    GitChangesRepositoryRegistry registry,
    GitRepositoryWatcherManager watcherManager) : ICommandHandler<GetGitChangeStatusRequest, GetGitChangeStatusResponse>
{
    public async Task<GetGitChangeStatusResponse> ExecuteAsync(GetGitChangeStatusRequest request, CancellationToken cancellationToken = default)
    {
        var workspaceName = request.WorkspaceName ?? throw new ArgumentException("workspaceName required");
        var repositoryName = request.RepositoryName ?? throw new ArgumentException("repositoryName required");

        var workspacePath = git.GetWorkspacePath(request.WorkspaceRoot!, workspaceName);
        var repoPath = Path.Combine(workspacePath, repositoryName);

        if (!git.DirectoryExists(repoPath))
        {
            return new GetGitChangeStatusResponse { Success = false, ErrorCode = "RepositoryNotFound", ErrorMessage = "Repository not found." };
        }

        registry.Register(repoPath, request.WorkspaceId, request.RepositoryId);

        using var lease = watcherManager.Acquire(repoPath);
        var result = await coordinator.RefreshNowAsync(repoPath, cancellationToken);

        return new GetGitChangeStatusResponse
        {
            Success = result.Success,
            ErrorCode = result.ErrorCode,
            ErrorMessage = result.ErrorMessage,
            Snapshot = result.Snapshot,
        };
    }
}
