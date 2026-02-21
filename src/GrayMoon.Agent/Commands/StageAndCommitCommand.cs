using GrayMoon.Agent.Abstractions;
using GrayMoon.Agent.Jobs.Requests;
using GrayMoon.Agent.Jobs.Response;

namespace GrayMoon.Agent.Commands;

public sealed class StageAndCommitCommand(IGitService git) : ICommandHandler<StageAndCommitRequest, StageAndCommitResponse>
{
    public async Task<StageAndCommitResponse> ExecuteAsync(StageAndCommitRequest request, CancellationToken cancellationToken = default)
    {
        var workspaceName = request.WorkspaceName ?? throw new ArgumentException("workspaceName required");
        var repositoryName = request.RepositoryName ?? throw new ArgumentException("repositoryName required");
        var commitMessage = request.CommitMessage ?? throw new ArgumentException("commitMessage required");
        var pathsToStage = request.PathsToStage ?? [];

        var workspacePath = git.GetWorkspacePath(workspaceName);
        var repoPath = Path.Combine(workspacePath, repositoryName);

        if (!git.DirectoryExists(repoPath))
            return new StageAndCommitResponse { Success = false, ErrorMessage = "Repository not found." };

        var (success, errorMessage) = await git.StageAndCommitAsync(repoPath, pathsToStage.ToList(), commitMessage, cancellationToken);
        return new StageAndCommitResponse { Success = success, ErrorMessage = errorMessage };
    }
}
