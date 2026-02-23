using GrayMoon.Agent.Abstractions;
using GrayMoon.Agent.Jobs.Requests;
using GrayMoon.Agent.Jobs.Response;
using GrayMoon.Agent.Models;

namespace GrayMoon.Agent.Commands;

public sealed class CreateBranchCommand(IGitService git) : ICommandHandler<CreateBranchRequest, CreateBranchResponse>
{
    public async Task<CreateBranchResponse> ExecuteAsync(CreateBranchRequest request, CancellationToken cancellationToken = default)
    {
        var workspaceName = request.WorkspaceName ?? throw new ArgumentException("workspaceName required");
        var repositoryName = request.RepositoryName ?? throw new ArgumentException("repositoryName required");
        var newBranchName = request.NewBranchName ?? throw new ArgumentException("newBranchName required");
        var baseBranchName = request.BaseBranchName ?? throw new ArgumentException("baseBranchName required");

        var workspacePath = git.GetWorkspacePath(workspaceName);
        var repoPath = Path.Combine(workspacePath, repositoryName);

        if (!git.DirectoryExists(repoPath))
        {
            return new CreateBranchResponse
            {
                Success = false,
                ErrorMessage = "Repository not found"
            };
        }

        var (success, errorMessage) = await git.CreateBranchAsync(repoPath, newBranchName, baseBranchName, cancellationToken);
        if (!success)
        {
            return new CreateBranchResponse
            {
                Success = false,
                ErrorMessage = errorMessage ?? "Failed to create branch"
            };
        }

        string? currentBranch = null;
        var (versionResult, _) = await git.GetVersionAsync(repoPath, cancellationToken);
        if (versionResult != null)
        {
            currentBranch = versionResult.BranchName ?? versionResult.EscapedBranchName;
        }

        return new CreateBranchResponse
        {
            Success = true,
            CurrentBranch = currentBranch
        };
    }
}
