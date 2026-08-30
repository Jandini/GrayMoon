namespace GrayMoon.App.Services.Application;

public interface IWorkspaceUpdateOperations
{
    Task<IReadOnlySet<int>> UpdateAsync(
        int workspaceId,
        CancellationToken cancellationToken,
        IProgress<OperationProgress>? progress,
        Action<int, string> setRepositoryError,
        IReadOnlySet<int>? repoIdsToUpdate = null,
        string? commitMessage = null,
        bool includeDepsInCommitMessage = true,
        int? maxLevel = null,
        string? runId = null);

    Task<int> RestorePackagesAsync(
        int workspaceId,
        Action<string> reportProgress,
        CancellationToken cancellationToken);
}

public sealed class WorkspaceUpdateOperations(
    WorkspaceUpdateHandler updateHandler,
    WorkspaceGitService workspaceGitService) : IWorkspaceUpdateOperations
{
    public Task<IReadOnlySet<int>> UpdateAsync(
        int workspaceId,
        CancellationToken cancellationToken,
        IProgress<OperationProgress>? progress,
        Action<int, string> setRepositoryError,
        IReadOnlySet<int>? repoIdsToUpdate = null,
        string? commitMessage = null,
        bool includeDepsInCommitMessage = true,
        int? maxLevel = null,
        string? runId = null)
        => updateHandler.RunUpdateAsync(
            workspaceId,
            cancellationToken,
            progress,
            setRepositoryError,
            repoIdsToUpdate: repoIdsToUpdate,
            commitMessage: commitMessage,
            includeDepsInCommitMessage: includeDepsInCommitMessage,
            maxLevel: maxLevel,
            runId: runId);

    public Task<int> RestorePackagesAsync(
        int workspaceId,
        Action<string> reportProgress,
        CancellationToken cancellationToken)
        => workspaceGitService.RestoreAllWorkspacePackagesAsync(workspaceId, reportProgress, cancellationToken);
}
