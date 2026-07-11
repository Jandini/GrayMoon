using GrayMoon.Agent.Abstractions;
using GrayMoon.Agent.Jobs.Requests;
using GrayMoon.Agent.Jobs.Response;
using GrayMoon.Common.Git;

namespace GrayMoon.Agent.Commands;

public sealed class GetGitFileDiffCommand(IGitService git, IRepositoryGitChangesService gitChangesService)
    : ICommandHandler<GetGitFileDiffRequest, GetGitFileDiffResponse>
{
    public async Task<GetGitFileDiffResponse> ExecuteAsync(GetGitFileDiffRequest request, CancellationToken cancellationToken = default)
    {
        var workspaceName = request.WorkspaceName ?? throw new ArgumentException("workspaceName required");
        var repositoryName = request.RepositoryName ?? throw new ArgumentException("repositoryName required");

        var workspacePath = git.GetWorkspacePath(request.WorkspaceRoot!, workspaceName);
        var repoPath = Path.Combine(workspacePath, repositoryName);

        if (!git.DirectoryExists(repoPath))
        {
            return new GetGitFileDiffResponse { Success = false, ErrorMessage = "Repository not found." };
        }

        var comparison = (GitDiffComparison)request.Comparison;
        var diff = await gitChangesService.GetDiffAsync(repoPath, new GitDiffRequest(request.Path, comparison), cancellationToken);

        return new GetGitFileDiffResponse
        {
            Success = diff.State != GitDiffContentState.Error,
            ErrorMessage = diff.ErrorMessage,
            Diff = diff,
        };
    }
}
