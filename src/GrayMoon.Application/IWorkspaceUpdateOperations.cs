namespace GrayMoon.Application;

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
