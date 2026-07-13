using GrayMoon.Agent.Abstractions;
using GrayMoon.Agent.Jobs.Requests;
using GrayMoon.Agent.Jobs.Response;
using GrayMoon.Agent.Services.GitChanges;

namespace GrayMoon.Agent.Commands;

public sealed class CommitGitChangesCommand(IGitService git, IRepositoryGitChangesService gitChangesService, GitChangesSnapshotCache snapshotCache)
    : ICommandHandler<CommitGitChangesRequest, CommitGitChangesResponse>
{
    public async Task<CommitGitChangesResponse> ExecuteAsync(CommitGitChangesRequest request, CancellationToken cancellationToken = default)
    {
        var workspaceName = request.WorkspaceName ?? throw new ArgumentException("workspaceName required");
        var repositoryName = request.RepositoryName ?? throw new ArgumentException("repositoryName required");
        var commitMessage = request.CommitMessage ?? throw new ArgumentException("commitMessage required");

        var workspacePath = git.GetWorkspacePath(request.WorkspaceRoot!, workspaceName);
        var repoPath = Path.Combine(workspacePath, repositoryName);

        if (!git.DirectoryExists(repoPath))
        {
            return new CommitGitChangesResponse { Success = false, ErrorCode = "RepositoryNotFound", ErrorMessage = "Repository not found." };
        }

        var operationRequest = new GitCommitOperationRequest(commitMessage, request.StageAllFirst);
        var nextVersion = snapshotCache.NextVersion(repoPath);

        var result = await gitChangesService.CommitAsync(repoPath, operationRequest, nextVersion, cancellationToken);
        if (result.Snapshot != null)
        {
            snapshotCache.SetLatest(repoPath, result.Snapshot);
        }

        return new CommitGitChangesResponse
        {
            Success = result.Success,
            ErrorCode = result.ErrorCode,
            ErrorMessage = result.ErrorMessage,
            CommitSha = result.CommitSha,
            Snapshot = result.Snapshot,
        };
    }
}
