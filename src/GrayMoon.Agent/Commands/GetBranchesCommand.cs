using GrayMoon.Agent.Abstractions;
using GrayMoon.Agent.Jobs.Requests;
using GrayMoon.Agent.Jobs.Response;

namespace GrayMoon.Agent.Commands;

public sealed class GetBranchesCommand(IGitService git) : ICommandHandler<GetBranchesRequest, GetBranchesResponse>
{
    public async Task<GetBranchesResponse> ExecuteAsync(GetBranchesRequest request, CancellationToken cancellationToken = default)
    {
        var workspaceName = request.WorkspaceName ?? throw new ArgumentException("workspaceName required");
        var repositoryName = request.RepositoryName ?? throw new ArgumentException("repositoryName required");

        var workspacePath = git.GetWorkspacePath(request.WorkspaceRoot!, workspaceName);
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
        var (fetchSuccess, fetchError) = await git.FetchAsync(repoPath, includeTags: true, bearerToken: null, cancellationToken);
        if (!fetchSuccess)
        {
            return new GetBranchesResponse
            {
                LocalBranches = Array.Empty<string>(),
                RemoteBranches = Array.Empty<string>(),
                ErrorMessage = fetchError ?? "Fetch failed"
            };
        }

        var localBranches = await git.GetLocalBranchesAsync(repoPath, cancellationToken);
        var remoteBranches = await git.GetRemoteBranchesFromRefsAsync(repoPath, cancellationToken);
        var defaultBranch = await git.GetDefaultBranchNameAsync(repoPath, cancellationToken);
        var currentBranch = await git.GetCurrentBranchNameAsync(repoPath, cancellationToken);

        return new GetBranchesResponse
        {
            LocalBranches = localBranches,
            RemoteBranches = remoteBranches,
            CurrentBranch = currentBranch,
            DefaultBranch = defaultBranch
        };
    }
}
