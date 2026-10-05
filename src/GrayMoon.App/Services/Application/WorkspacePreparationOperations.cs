using GrayMoon.Application.Features;

namespace GrayMoon.App.Services.Application;

public sealed class WorkspacePreparationOperations(PrepareWorkspaceOrchestrator orchestrator) : IWorkspacePreparationOperations
{
    public Task<DependencyUpdateRunResult> PrepareAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
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
            contextId,
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
