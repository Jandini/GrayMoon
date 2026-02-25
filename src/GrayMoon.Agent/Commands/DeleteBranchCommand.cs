using GrayMoon.Agent.Abstractions;
using GrayMoon.Agent.Jobs.Requests;
using GrayMoon.Agent.Jobs.Response;

namespace GrayMoon.Agent.Commands;

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

        var workspacePath = git.GetWorkspacePath(request.WorkspaceRoot!, workspaceName);
        var repoPath = Path.Combine(workspacePath, repositoryName);

        if (!git.DirectoryExists(repoPath))
        {
            return new DeleteBranchResponse
            {
                Success = false,
                ErrorMessage = "Repository not found."
            };
        }

        try
        {
            var (success, errorMessage) = await git.DeleteBranchAsync(repoPath, branchName, request.IsRemote, cancellationToken);
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
