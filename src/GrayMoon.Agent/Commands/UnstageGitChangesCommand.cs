using GrayMoon.Agent.Abstractions;
using GrayMoon.Agent.Jobs.Requests;
using GrayMoon.Agent.Jobs.Response;
using GrayMoon.Agent.Services.GitChanges;
using GrayMoon.Common.Git;

namespace GrayMoon.Agent.Commands;

public sealed class UnstageGitChangesCommand(IGitService git, IRepositoryGitChangesService gitChangesService, GitChangesSnapshotCache snapshotCache)
    : ICommandHandler<UnstageGitChangesRequest, GitMutationResponse>
{
    public async Task<GitMutationResponse> ExecuteAsync(UnstageGitChangesRequest request, CancellationToken cancellationToken = default)
    {
        var workspaceName = request.WorkspaceName ?? throw new ArgumentException("workspaceName required");
        var repositoryName = request.RepositoryName ?? throw new ArgumentException("repositoryName required");

        var workspacePath = git.GetWorkspacePath(request.WorkspaceRoot!, workspaceName);
        var repoPath = Path.Combine(workspacePath, repositoryName);

        if (!git.DirectoryExists(repoPath))
        {
            return new GitMutationResponse { Success = false, ErrorCode = "RepositoryNotFound", ErrorMessage = "Repository not found." };
        }

        var scope = (GitChangeOperationScope)request.Scope;
        var operationRequest = new GitStageOperationRequest(scope, request.Paths ?? []);
        var nextVersion = snapshotCache.NextVersion(repoPath);

        var result = await gitChangesService.UnstageAsync(repoPath, operationRequest, nextVersion, cancellationToken);
        if (result.Snapshot != null)
        {
            snapshotCache.SetLatest(repoPath, result.Snapshot);
        }

        return new GitMutationResponse
        {
            Success = result.Success,
            ErrorCode = result.ErrorCode,
            ErrorMessage = result.ErrorMessage,
            Snapshot = result.Snapshot,
        };
    }
}
