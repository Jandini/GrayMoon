using GrayMoon.Agent.Abstractions;
using GrayMoon.Agent.Jobs.Requests;
using GrayMoon.Agent.Jobs.Response;
using GrayMoon.Agent.Models;

namespace GrayMoon.Agent.Commands;

public sealed class GetBranchesCommand(IGitService git) : ICommandHandler<GetBranchesRequest, GetBranchesResponse>
{
    public async Task<GetBranchesResponse> ExecuteAsync(GetBranchesRequest request, CancellationToken cancellationToken = default)
    {
        var workspaceName = request.WorkspaceName ?? throw new ArgumentException("workspaceName required");
        var repositoryName = request.RepositoryName ?? throw new ArgumentException("repositoryName required");

        var workspacePath = git.GetWorkspacePath(workspaceName);
        var repoPath = Path.Combine(workspacePath, repositoryName);

        if (!git.DirectoryExists(repoPath))
        {
            return new GetBranchesResponse
            {
                LocalBranches = Array.Empty<string>(),
                RemoteBranches = Array.Empty<string>()
            };
        }

        // Fetch to ensure remote branches are up to date
        await git.FetchAsync(repoPath, includeTags: false, bearerToken: null, cancellationToken);

        var localBranches = await git.GetLocalBranchesAsync(repoPath, cancellationToken);
        var remoteBranches = await git.GetRemoteBranchesAsync(repoPath, cancellationToken);
        var defaultBranch = await git.GetDefaultBranchNameAsync(repoPath, cancellationToken);

        // Get current branch
        string? currentBranch = null;
        var versionResult = await git.GetVersionAsync(repoPath, cancellationToken);
        if (versionResult != null)
        {
            currentBranch = versionResult.BranchName ?? versionResult.EscapedBranchName;
        }

        return new GetBranchesResponse
        {
            LocalBranches = localBranches,
            RemoteBranches = remoteBranches,
            CurrentBranch = currentBranch,
            DefaultBranch = defaultBranch
        };
    }
}
