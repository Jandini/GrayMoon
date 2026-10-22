using GrayMoon.Worker.Abstractions;
using GrayMoon.Worker.Jobs.Requests;
using GrayMoon.Worker.Jobs.Response;
using GrayMoon.Worker.Services;
using GrayMoon.Worker.Services.GitChanges;

namespace GrayMoon.Worker.Commands;

public sealed class StageAndCommitCommand(IGitService git, IGitRepositoryReader reader, GitStatusRefreshCoordinator refreshCoordinator) : ICommandHandler<StageAndCommitRequest, StageAndCommitResponse>
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

        // A dependency update under central package management edits Directory.Packages.props, not the project, so the
        // props file that governs each staged project is staged with it. Staging an unchanged file is a no-op.
        var stagePaths = pathsToStage.ToList();
        foreach (var path in pathsToStage.ToList())
        {
            if (string.IsNullOrWhiteSpace(path) || !path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                continue;
            var props = CentralPackageVersions.FindPropsFile(Path.Combine(repoPath, path), repoPath);
            if (props == null)
                continue;
            var relative = Path.GetRelativePath(repoPath, props).Replace('\\', '/');
            if (!stagePaths.Contains(relative, StringComparer.OrdinalIgnoreCase))
                stagePaths.Add(relative);
        }

        var (success, committed, errorMessage) = await git.StageAndCommitAsync(repoPath, stagePaths, commitMessage, cancellationToken, skipHooks: request.SkipHooks);
        // Publish the post-commit Changes snapshot now. Waiting for the watcher (debounce + scan queue behind other
        // repositories) leaves the just-staged version files shown as staged long after the commit completed.
        try
        {
            await refreshCoordinator.RefreshNowAsync(repoPath, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Best effort: the watcher still refreshes this repository.
        }

        return new StageAndCommitResponse { Success = success, Committed = committed, ErrorMessage = errorMessage };
    }
}
