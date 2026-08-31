namespace GrayMoon.App.Services.Orchestration;

/// <summary>
/// Sequences the New Feature workflow: parallel branch creation with inline state sync (hooks
/// suppressed), then dependency update. By the time each phase completes, the database reflects
/// full, consistent state - no async hook syncs to race against.
/// </summary>
public sealed class NewFeatureOrchestrator(
    WorkspaceBranchHandler branchHandler,
    DependencyUpdateOrchestrator dependencyUpdateOrchestrator,
    ILogger<NewFeatureOrchestrator> logger)
{
    public async Task<IReadOnlySet<int>> RunAsync(
        int workspaceId,
        string newBranchName,
        string baseBranch,
        IReadOnlySet<int>? repositoryIds,
        bool updateDependencies,
        string? commitMessage,
        IProgress<OperationProgress>? progress,
        Action<int, string> setRepositoryError,
        CancellationToken cancellationToken)
    {
        logger.LogInformation("NewFeatureOrchestrator starting for workspace {WorkspaceId}: branch={Branch}, updateDeps={UpdateDeps}", workspaceId, newBranchName, updateDependencies);

        progress.Report("Creating branches...");
        var branchErrors = await branchHandler.CreateBranchesAsync(
            workspaceId,
            newBranchName,
            baseBranch,
            repositoryIds,
            progress,
            syncState: true,
            cancellationToken);
        foreach (var (repoId, msg) in branchErrors)
            setRepositoryError(repoId, msg);

        IReadOnlySet<int> syncedRepoIds = new HashSet<int>();
        if (updateDependencies)
        {
            progress.Report("Updating dependencies...");
            syncedRepoIds = await dependencyUpdateOrchestrator.RunAsync(
                workspaceId,
                cancellationToken,
                progress,
                setRepositoryError,
                onAppSideComplete: null,
                repoIdsToUpdate: null,
                commitMessage: commitMessage,
                includeDepsInCommitMessage: true);
        }

        logger.LogInformation("NewFeatureOrchestrator completed for workspace {WorkspaceId}", workspaceId);
        return syncedRepoIds;
    }
}
