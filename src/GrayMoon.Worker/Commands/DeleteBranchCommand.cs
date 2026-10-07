using GrayMoon.Worker.Abstractions;
using GrayMoon.Worker.Jobs.Requests;
using GrayMoon.Worker.Jobs.Response;
using GrayMoon.Worker.Services;

namespace GrayMoon.Worker.Commands;

public sealed class DeleteBranchCommand(IGitService git) : ICommandHandler<DeleteBranchRequest, DeleteBranchResponse>
{
    public async Task<DeleteBranchResponse> ExecuteAsync(DeleteBranchRequest request, CancellationToken cancellationToken = default)
    {
        var workspaceName = request.WorkspaceName ?? throw new ArgumentException("workspaceName required");
        var repositoryName = request.RepositoryName ?? throw new ArgumentException("repositoryName required");
        var branchName = request.BranchName?.Trim();
        if (string.IsNullOrWhiteSpace(branchName))
            throw new ArgumentException("branchName required");
        if (string.IsNullOrWhiteSpace(request.WorkspaceRoot))
            throw new ArgumentException("workspaceRoot required");

        var workspacePath = WorkerRepositoryPaths.GetWorkspacePath(request.WorkspaceRoot!, workspaceName);
        var repoPath = WorkerRepositoryPaths.Resolve(workspacePath, repositoryName, request.WorkspaceRepositoryName);

        if (!Directory.Exists(repoPath))
        {
            return new DeleteBranchResponse
            {
                Success = false,
                ErrorMessage = "Repository not found."
            };
        }

        try
        {
            var (success, errorMessage) = await git.DeleteBranchAsync(
                repoPath, branchName, request.IsRemote, request.Force, cancellationToken,
                bearerToken: request.BearerToken, expectedSha: request.ExpectedSha);
            return new DeleteBranchResponse
            {
                Success = success,
                ErrorMessage = errorMessage
            };
        }
        catch (Exception ex)
        {
            return new DeleteBranchResponse
            {
                Success = false,
                ErrorMessage = ex.Message
            };
        }
    }
}
