namespace GrayMoon.Application;

public interface IWorkspaceFeatureOperations
{
    Task<DependencyUpdateRunResult> CreateAsync(
        int workspaceId,
        string newBranchName,
        string baseBranch,
        IReadOnlySet<int>? repositoryIds,
        bool updateDependencies,
        string? commitMessage,
        IProgress<OperationProgress>? progress,
        Action<int, string> setRepositoryError,
        CancellationToken cancellationToken);
}
