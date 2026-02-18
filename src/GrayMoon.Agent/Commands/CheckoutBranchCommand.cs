using GrayMoon.Agent.Abstractions;
using GrayMoon.Agent.Jobs.Requests;
using GrayMoon.Agent.Jobs.Response;
using GrayMoon.Agent.Models;

namespace GrayMoon.Agent.Commands;

public sealed class CheckoutBranchCommand(IGitService git) : ICommandHandler<CheckoutBranchRequest, CheckoutBranchResponse>
{
    public async Task<CheckoutBranchResponse> ExecuteAsync(CheckoutBranchRequest request, CancellationToken cancellationToken = default)
    {
        var workspaceName = request.WorkspaceName ?? throw new ArgumentException("workspaceName required");
        var repositoryName = request.RepositoryName ?? throw new ArgumentException("repositoryName required");
        var branchName = request.BranchName ?? throw new ArgumentException("branchName required");

        var workspacePath = git.GetWorkspacePath(workspaceName);
        var repoPath = Path.Combine(workspacePath, repositoryName);

        if (!git.DirectoryExists(repoPath))
        {
            return new CheckoutBranchResponse
            {
                Success = false,
                ErrorMessage = "Repository not found"
            };
        }

        var (success, errorMessage) = await git.CheckoutBranchAsync(repoPath, branchName, cancellationToken);
        if (!success)
        {
            return new CheckoutBranchResponse
            {
                Success = false,
                ErrorMessage = errorMessage ?? "Failed to checkout branch"
            };
        }

        // Get current branch after checkout
        string? currentBranch = null;
        var versionResult = await git.GetVersionAsync(repoPath, cancellationToken);
        if (versionResult != null)
        {
            currentBranch = versionResult.BranchName ?? versionResult.EscapedBranchName;
        }

        return new CheckoutBranchResponse
        {
            Success = true,
            CurrentBranch = currentBranch
        };
    }
}
