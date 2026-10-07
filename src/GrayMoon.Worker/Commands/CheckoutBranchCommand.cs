using GrayMoon.Worker.Abstractions;
using GrayMoon.Worker.Jobs.Requests;
using GrayMoon.Worker.Jobs.Response;
using GrayMoon.Worker.Services;

namespace GrayMoon.Worker.Commands;

public sealed class CheckoutBranchCommand(IGitService git) : ICommandHandler<CheckoutBranchRequest, CheckoutBranchResponse>
{
    public async Task<CheckoutBranchResponse> ExecuteAsync(CheckoutBranchRequest request, CancellationToken cancellationToken = default)
    {
        var workspaceName = request.WorkspaceName ?? throw new ArgumentException("workspaceName required");
        var repositoryName = request.RepositoryName ?? throw new ArgumentException("repositoryName required");
        var branchName = request.BranchName ?? throw new ArgumentException("branchName required");

        var workspacePath = WorkerRepositoryPaths.GetWorkspacePath(request.WorkspaceRoot!, workspaceName);
        var repoPath = WorkerRepositoryPaths.Resolve(workspacePath, repositoryName, request.WorkspaceRepositoryName);

        if (!Directory.Exists(repoPath))
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
