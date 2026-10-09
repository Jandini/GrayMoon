using GrayMoon.Worker.Abstractions;
using GrayMoon.Worker.Jobs.Requests;
using GrayMoon.Worker.Jobs.Response;
using GrayMoon.Worker.Services.GitChanges;
using GrayMoon.Worker.Services;

namespace GrayMoon.Worker.Commands;

public sealed class CommitGitChangesCommand(IRepositoryGitChangesService gitChangesService, GitChangesSnapshotCache snapshotCache)
    : ICommandHandler<CommitGitChangesRequest, CommitGitChangesResponse>
{
    public async Task<CommitGitChangesResponse> ExecuteAsync(CommitGitChangesRequest request, CancellationToken cancellationToken = default)
    {
        var workspaceName = request.WorkspaceName ?? throw new ArgumentException("workspaceName required");
        var repositoryName = request.RepositoryName ?? throw new ArgumentException("repositoryName required");
        var commitMessage = request.CommitMessage ?? throw new ArgumentException("commitMessage required");

        var workspacePath = WorkerRepositoryPaths.GetWorkspacePath(request.WorkspaceRoot!, workspaceName);
        var repoPath = WorkerRepositoryPaths.Resolve(workspacePath, repositoryName, request.WorkspaceRepositoryName);

        if (!Directory.Exists(repoPath))
        {
            return new CommitGitChangesResponse { Success = false, ErrorCode = "RepositoryNotFound", ErrorMessage = "Repository not found." };
        }

        var operationRequest = new GitCommitOperationRequest(commitMessage, request.StageAllFirst);
        var nextVersion = snapshotCache.NextVersion(repoPath);

        var result = await gitChangesService.CommitAsync(repoPath, operationRequest, nextVersion, cancellationToken);
        // Version taken after the git work finished: a watcher scan that started mid-mutation holds a lower one, so its
        // half-done view can never outrank this snapshot (the App rejects versions <= what it already persisted).
        var snapshot = snapshotCache.StampAfterMutation(repoPath, result.Snapshot);
        if (snapshot != null)
        {
            snapshotCache.SetLatest(repoPath, snapshot);
        }

        return new CommitGitChangesResponse
        {
            Success = result.Success,
            ErrorCode = result.ErrorCode,
            ErrorMessage = result.ErrorMessage,
            CommitSha = result.CommitSha,
            Snapshot = snapshot,
        };
    }
}
