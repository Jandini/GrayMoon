using GrayMoon.Worker.Abstractions;
using GrayMoon.Worker.Jobs.Requests;
using GrayMoon.Worker.Jobs.Response;
using GrayMoon.Worker.Services;

namespace GrayMoon.Worker.Commands;

public sealed class StageAndCommitCommand(IGitService git, IGitRepositoryReader reader) : ICommandHandler<StageAndCommitRequest, StageAndCommitResponse>
{
    public async Task<StageAndCommitResponse> ExecuteAsync(StageAndCommitRequest request, CancellationToken cancellationToken = default)
    {
        var workspaceName = request.WorkspaceName ?? throw new ArgumentException("workspaceName required");
        var repositoryName = request.RepositoryName ?? throw new ArgumentException("repositoryName required");
        var commitMessage = request.CommitMessage ?? throw new ArgumentException("commitMessage required");
        var pathsToStage = request.PathsToStage ?? [];

        var workspacePath = WorkerRepositoryPaths.GetWorkspacePath(request.WorkspaceRoot!, workspaceName);
        var repoPath = WorkerRepositoryPaths.Resolve(workspacePath, repositoryName, request.WorkspaceRepositoryName);

        if (!Directory.Exists(repoPath))
            return new StageAndCommitResponse { Success = false, ErrorMessage = "Repository not found." };

        var checkedOutTag = await reader.GetCheckedOutTagAsync(repoPath, cancellationToken);
        if (checkedOutTag != null)
            return new StageAndCommitResponse { Success = true, Committed = false };

        var (success, committed, errorMessage) = await git.StageAndCommitAsync(repoPath, pathsToStage.ToList(), commitMessage, cancellationToken, skipHooks: request.SkipHooks);
        return new StageAndCommitResponse { Success = success, Committed = committed, ErrorMessage = errorMessage };
    }
}
