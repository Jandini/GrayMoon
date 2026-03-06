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

        var workspacePath = git.GetWorkspacePath(request.WorkspaceRoot!, workspaceName);
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
        var (fetchSuccess, fetchError) = await git.FetchAsync(repoPath, includeTags: true, bearerToken: null, cancellationToken);
        if (!fetchSuccess)
        {
            return new RefreshBranchesResponse
            {
                Success = false,
                ErrorMessage = fetchError ?? "Fetch failed",
                LocalBranches = Array.Empty<string>(),
                RemoteBranches = Array.Empty<string>()
            };
        }

        var localBranches = await git.GetLocalBranchesAsync(repoPath, cancellationToken);
        var remoteBranches = await git.GetRemoteBranchesFromRefsAsync(repoPath, cancellationToken);
        var defaultBranch = await git.GetDefaultBranchNameAsync(repoPath, cancellationToken);
        var currentBranch = await git.GetCurrentBranchNameAsync(repoPath, cancellationToken);

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
