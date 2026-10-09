using GrayMoon.Worker.Abstractions;
using GrayMoon.Worker.Jobs.Requests;
using GrayMoon.Worker.Jobs.Response;
using GrayMoon.Worker.Services.GitChanges;
using GrayMoon.Common.Git;
using GrayMoon.Worker.Services;

namespace GrayMoon.Worker.Commands;

public sealed class StageGitChangesCommand(IRepositoryGitChangesService gitChangesService, GitChangesSnapshotCache snapshotCache)
    : ICommandHandler<StageGitChangesRequest, GitMutationResponse>
{
    public async Task<GitMutationResponse> ExecuteAsync(StageGitChangesRequest request, CancellationToken cancellationToken = default)
    {
        var workspaceName = request.WorkspaceName ?? throw new ArgumentException("workspaceName required");
        var repositoryName = request.RepositoryName ?? throw new ArgumentException("repositoryName required");

        var workspacePath = WorkerRepositoryPaths.GetWorkspacePath(request.WorkspaceRoot!, workspaceName);
        var repoPath = WorkerRepositoryPaths.Resolve(workspacePath, repositoryName, request.WorkspaceRepositoryName);

        if (!Directory.Exists(repoPath))
        {
            return new GitMutationResponse { Success = false, ErrorCode = "RepositoryNotFound", ErrorMessage = "Repository not found." };
        }

        var scope = (GitChangeOperationScope)request.Scope;
        var operationRequest = new GitStageOperationRequest(scope, request.Paths ?? []);
        var nextVersion = snapshotCache.NextVersion(repoPath);

        var result = await gitChangesService.StageAsync(repoPath, operationRequest, nextVersion, cancellationToken);
        // Version taken after the git work finished: a watcher scan that started mid-mutation holds a lower one, so its
        // half-done view can never outrank this snapshot (the App rejects versions <= what it already persisted).
        var snapshot = snapshotCache.StampAfterMutation(repoPath, result.Snapshot);
        if (snapshot != null)
        {
            snapshotCache.SetLatest(repoPath, snapshot);
        }

        return new GitMutationResponse
        {
            Success = result.Success,
            ErrorCode = result.ErrorCode,
            ErrorMessage = result.ErrorMessage,
            Snapshot = snapshot,
        };
    }
}
