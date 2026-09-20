namespace GrayMoon.Application;

public interface IWorkspacePreparationOperations
{
    Task<DependencyUpdateRunResult> PrepareAsync(
        int workspaceId,
        string newBranchName,
        string baseBranch,
        IReadOnlySet<int>? repositoryIds,
        bool updateDependencies,
        string? commitMessage,
        IProgress<OperationProgress>? progress,
        Action<int, string> setRepositoryError,
        Action<int, string> setLevelError,
        CancellationToken cancellationToken);
}
