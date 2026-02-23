using GrayMoon.Agent.Abstractions;
using GrayMoon.Agent.Jobs.Requests;
using GrayMoon.Agent.Jobs.Response;
using GrayMoon.Agent.Models;

namespace GrayMoon.Agent.Commands;

public sealed class RefreshBranchesCommand(IGitService git) : ICommandHandler<RefreshBranchesRequest, RefreshBranchesResponse>
{
    public async Task<RefreshBranchesResponse> ExecuteAsync(RefreshBranchesRequest request, CancellationToken cancellationToken = default)
    {
        var workspaceName = request.WorkspaceName ?? throw new ArgumentException("workspaceName required");
        var repositoryName = request.RepositoryName ?? throw new ArgumentException("repositoryName required");

        var workspacePath = git.GetWorkspacePath(workspaceName);
        var repoPath = Path.Combine(workspacePath, repositoryName);

        if (!git.DirectoryExists(repoPath))
        {
            return new RefreshBranchesResponse
            {
                Success = false,
                ErrorMessage = "Repository not found"
            };
        }

        // Fetch to ensure remote branches are up to date
        await git.FetchAsync(repoPath, includeTags: false, bearerToken: null, cancellationToken);

        var localBranches = await git.GetLocalBranchesAsync(repoPath, cancellationToken);
        var remoteBranches = await git.GetRemoteBranchesAsync(repoPath, cancellationToken);
        var defaultBranch = await git.GetDefaultBranchNameAsync(repoPath, cancellationToken);

        // Get current branch
        string? currentBranch = null;
        var (versionResult, _) = await git.GetVersionAsync(repoPath, cancellationToken);
        if (versionResult != null)
        {
            currentBranch = versionResult.BranchName ?? versionResult.EscapedBranchName;
        }

        return new RefreshBranchesResponse
        {
            Success = true,
            LocalBranches = localBranches,
            RemoteBranches = remoteBranches,
            CurrentBranch = currentBranch,
            DefaultBranch = defaultBranch
        };
    }
}
