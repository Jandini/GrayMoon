namespace GrayMoon.App.Services.Application;

public sealed class WorkspaceFeatureOperations(NewFeatureOrchestrator orchestrator) : IWorkspaceFeatureOperations
{
    public Task<IReadOnlySet<int>> CreateAsync(
        int workspaceId,
        string newBranchName,
        string baseBranch,
        IReadOnlySet<int>? repositoryIds,
        bool updateDependencies,
        string? commitMessage,
        IProgress<OperationProgress>? progress,
        Action<int, string> setRepositoryError,
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
            cancellationToken);
}
