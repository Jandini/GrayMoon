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

        var workspacePath = git.GetWorkspacePath(request.WorkspaceRoot!, workspaceName);
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

        // Return current branch name without running GitVersion; the checkout hook will run and send SyncCommand with version, branch, and hasUpstream
        var currentBranch = branchName.StartsWith("origin/", StringComparison.OrdinalIgnoreCase)
            ? branchName.Substring("origin/".Length)
            : branchName;

        return new CheckoutBranchResponse
        {
            Success = true,
            CurrentBranch = currentBranch
        };
    }
}
