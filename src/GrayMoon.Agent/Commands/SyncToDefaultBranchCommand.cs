using GrayMoon.Agent.Abstractions;
using GrayMoon.Agent.Jobs.Requests;
using GrayMoon.Agent.Jobs.Response;

namespace GrayMoon.Agent.Commands;

public sealed class SyncToDefaultBranchCommand(IGitService git) : ICommandHandler<SyncToDefaultBranchRequest, SyncToDefaultBranchResponse>
{
    public async Task<SyncToDefaultBranchResponse> ExecuteAsync(SyncToDefaultBranchRequest request, CancellationToken cancellationToken = default)
    {
        var workspaceName = request.WorkspaceName ?? throw new ArgumentException("workspaceName required");
        var repositoryName = request.RepositoryName ?? throw new ArgumentException("repositoryName required");
        var currentBranchName = request.CurrentBranchName ?? throw new ArgumentException("currentBranchName required");

        var workspacePath = git.GetWorkspacePath(request.WorkspaceRoot!, workspaceName);
        var repoPath = Path.Combine(workspacePath, repositoryName);

        if (!git.DirectoryExists(repoPath))
        {
            return new SyncToDefaultBranchResponse
            {
                Success = false,
                ErrorMessage = "Repository not found"
            };
        }

        // Get default branch
        var defaultBranch = await git.GetDefaultBranchNameAsync(repoPath, cancellationToken);
        if (string.IsNullOrWhiteSpace(defaultBranch))
        {
            return new SyncToDefaultBranchResponse
            {
                Success = false,
                ErrorMessage = "Could not determine default branch"
            };
        }

        // Checkout default branch
        var (checkoutSuccess, checkoutError) = await git.CheckoutBranchAsync(repoPath, defaultBranch, cancellationToken);
        if (!checkoutSuccess)
        {
            return new SyncToDefaultBranchResponse
            {
                Success = false,
                ErrorMessage = checkoutError ?? "Failed to checkout default branch"
            };
        }

        // Delete the old branch (only if it's not the same as default)
        if (currentBranchName != defaultBranch)
        {
            // Force delete (-D) only when PR is merged (set by App from current PR status). Otherwise use -d so we only delete if merged locally.
            await git.DeleteLocalBranchAsync(repoPath, currentBranchName, force: request.ForceDeleteLocalBranch, cancellationToken);
        }

        // Always pull after switching to default so local is up to date
        var (pullSuccess, mergeConflict, pullError) = await git.PullAsync(repoPath, defaultBranch, request.BearerToken, cancellationToken);
        if (!pullSuccess)
        {
            return new SyncToDefaultBranchResponse
            {
                Success = false,
                ErrorMessage = mergeConflict ? (pullError ?? "Merge conflict after pull.") : (pullError ?? "Failed to pull after switching to default branch"),
                CurrentBranch = defaultBranch,
                DefaultBranch = defaultBranch
            };
        }

        return new SyncToDefaultBranchResponse
        {
            Success = true,
            CurrentBranch = defaultBranch,
            DefaultBranch = defaultBranch
        };
    }
}
