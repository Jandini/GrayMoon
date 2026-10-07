using GrayMoon.Worker.Abstractions;
using GrayMoon.Worker.Jobs.Requests;
using GrayMoon.Worker.Jobs.Response;
using GrayMoon.Worker.Services;

namespace GrayMoon.Worker.Commands;

public sealed class RefreshBranchesCommand(IGitService git, IRepositoryStateProbe stateProbe, IWorkerTokenProvider tokenProvider) : ICommandHandler<RefreshBranchesRequest, RefreshBranchesResponse>
{
    public async Task<RefreshBranchesResponse> ExecuteAsync(RefreshBranchesRequest request, CancellationToken cancellationToken = default)
    {
        var workspaceName = request.WorkspaceName ?? throw new ArgumentException("workspaceName required");
        var repositoryName = request.RepositoryName ?? throw new ArgumentException("repositoryName required");

        var workspacePath = WorkerRepositoryPaths.GetWorkspacePath(request.WorkspaceRoot!, workspaceName);
        var repoPath = WorkerRepositoryPaths.Resolve(workspacePath, repositoryName, request.WorkspaceRepositoryName);

        if (!Directory.Exists(repoPath))
        {
            return new RefreshBranchesResponse
            {
                Success = false,
                ErrorMessage = "Repository not found"
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
                return new RefreshBranchesResponse
                {
                    Success = false,
                    ErrorMessage = fetchError ?? "Fetch failed",
                    LocalBranches = Array.Empty<string>(),
                    RemoteBranches = Array.Empty<string>()
                };
            }
        }

        // The probe reports the branch's git-configured upstream alongside the lists, so read-only fetch
        // flows get the same answer the hook and sync flows do instead of matching names against remotes.
        var (state, _) = await stateProbe.CaptureAsync(repoPath, new RepositoryStateProbeOptions
        {
            IncludeBranchLists = true
        }, cancellationToken);

        return new RefreshBranchesResponse
        {
            Success = true,
            LocalBranches = state.LocalBranches ?? [],
            RemoteBranches = state.RemoteBranches ?? [],
            // When on a tag (detached HEAD), don't echo a fake branch name.
            CurrentBranch = state.BranchName,
            DefaultBranch = state.DefaultBranchName,
            Tags = state.Tags ?? [],
            CurrentTag = state.CheckedOutTag,
            HasUpstream = state.HasUpstream,
            UpstreamProbed = state.UpstreamProbed
        };
    }
}
