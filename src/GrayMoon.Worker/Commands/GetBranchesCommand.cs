using GrayMoon.Worker.Abstractions;
using GrayMoon.Worker.Jobs.Requests;
using GrayMoon.Worker.Jobs.Response;
using GrayMoon.Worker.Services;

namespace GrayMoon.Worker.Commands;

public sealed class GetBranchesCommand(IGitService git, IGitRepositoryReader reader, IWorkerTokenProvider tokenProvider) : ICommandHandler<GetBranchesRequest, GetBranchesResponse>
{
    public async Task<GetBranchesResponse> ExecuteAsync(GetBranchesRequest request, CancellationToken cancellationToken = default)
    {
        var workspaceName = request.WorkspaceName ?? throw new ArgumentException("workspaceName required");
        var repositoryName = request.RepositoryName ?? throw new ArgumentException("repositoryName required");

        var workspacePath = WorkerRepositoryPaths.GetWorkspacePath(request.WorkspaceRoot!, workspaceName);
        var repoPath = WorkerRepositoryPaths.Resolve(workspacePath, repositoryName, request.WorkspaceRepositoryName);

        if (!Directory.Exists(repoPath))
        {
            return new GetBranchesResponse
            {
                LocalBranches = Array.Empty<string>(),
                RemoteBranches = Array.Empty<string>()
            };
        }

        // Fetch to ensure remote branches are up to date. When a token is unavailable we skip the
        // remote fetch rather than contacting the remote without authentication.
        string? token = request.RepositoryId > 0
            ? await tokenProvider.GetTokenForRepositoryAsync(request.RepositoryId, cancellationToken)
            : null;
        if (token != null)
        {
            var (fetchSuccess, fetchError) = await git.FetchAsync(repoPath, includeTags: true, bearerToken: token, cancellationToken);
            if (!fetchSuccess)
            {
                return new GetBranchesResponse
                {
                    LocalBranches = Array.Empty<string>(),
                    RemoteBranches = Array.Empty<string>(),
                    ErrorMessage = fetchError ?? "Fetch failed"
                };
            }
        }

        var localBranches = await reader.GetLocalBranchesAsync(repoPath, cancellationToken);
        var remoteBranches = await reader.GetRemoteBranchesFromRefsAsync(repoPath, cancellationToken);
        var defaultBranch = await reader.GetDefaultBranchNameAsync(repoPath, cancellationToken);
        var currentBranch = await reader.GetCurrentBranchNameAsync(repoPath, cancellationToken);
        var tags = await reader.GetTagsAsync(repoPath, cancellationToken);
        var currentTag = await reader.GetCheckedOutTagAsync(repoPath, cancellationToken);

        return new GetBranchesResponse
        {
            LocalBranches = localBranches,
            RemoteBranches = remoteBranches,
            // When on a tag (detached HEAD), don't echo a fake branch name.
            CurrentBranch = currentTag == null ? currentBranch : null,
            DefaultBranch = defaultBranch,
            Tags = tags,
            CurrentTag = currentTag
        };
    }
}
