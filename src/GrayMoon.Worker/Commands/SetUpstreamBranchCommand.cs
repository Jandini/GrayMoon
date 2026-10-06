using GrayMoon.Worker.Abstractions;
using GrayMoon.Worker.Jobs.Requests;
using GrayMoon.Worker.Jobs.Response;
using GrayMoon.Worker.Services;

namespace GrayMoon.Worker.Commands;

public sealed class SetUpstreamBranchCommand(IGitService git, IWorkerTokenProvider tokenProvider) : ICommandHandler<SetUpstreamBranchRequest, SetUpstreamBranchResponse>
{
    public async Task<SetUpstreamBranchResponse> ExecuteAsync(SetUpstreamBranchRequest request, CancellationToken cancellationToken = default)
    {
        var workspaceName = request.WorkspaceName ?? throw new ArgumentException("workspaceName required");
        var repositoryName = request.RepositoryName ?? throw new ArgumentException("repositoryName required");
        var branchName = request.BranchName ?? throw new ArgumentException("branchName required");

        var workspacePath = git.GetWorkspacePath(request.WorkspaceRoot!, workspaceName);
        var repoPath = WorkerRepositoryPaths.Resolve(workspacePath, repositoryName, request.WorkspaceRepositoryName);

        if (!git.DirectoryExists(repoPath))
        {
            return new SetUpstreamBranchResponse
            {
                Success = false,
                ErrorMessage = "Repository not found"
            };
        }

        string? token = request.RepositoryId > 0
            ? await tokenProvider.GetTokenForRepositoryAsync(request.RepositoryId, cancellationToken)
            : null;
        if (token == null)
        {
            return new SetUpstreamBranchResponse
            {
                Success = false,
                ErrorMessage = "Connector token not available."
            };
        }

        var (success, errorMessage) = await git.PushAsync(repoPath, branchName, token, setTracking: true, cancellationToken);
        if (!success)
        {
            return new SetUpstreamBranchResponse
            {
                Success = false,
                ErrorMessage = errorMessage ?? "Failed to set upstream (push -u)"
            };
        }

        return new SetUpstreamBranchResponse { Success = true };
    }
}
