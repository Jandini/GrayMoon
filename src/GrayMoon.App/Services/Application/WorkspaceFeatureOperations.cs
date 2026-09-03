namespace GrayMoon.App.Services.Application;

public sealed class WorkspaceFeatureOperations(NewFeatureOrchestrator orchestrator) : IWorkspaceFeatureOperations
{
    public Task<DependencyUpdateRunResult> CreateAsync(
        int workspaceId,
        string newBranchName,
        string baseBranch,
        IReadOnlySet<int>? repositoryIds,
        bool updateDependencies,
        string? commitMessage,
        IProgress<OperationProgress>? progress,
        Action<int, string> setRepositoryError,
        Action<int, string> setLevelError,
        CancellationToken cancellationToken)
        => orchestrator.RunAsync(
            workspaceId,
            newBranchName,
            baseBranch,
            repositoryIds,
            updateDependencies,
            commitMessage,
            progress,
            setRepositoryError,
            setLevelError,
            cancellationToken);
}
