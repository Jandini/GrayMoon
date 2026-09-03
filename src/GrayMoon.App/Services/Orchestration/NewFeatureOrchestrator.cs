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
    public async Task<DependencyUpdateRunResult> RunAsync(
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
    {
        logger.LogInformation("NewFeatureOrchestrator starting for workspace {WorkspaceId}: branch={Branch}, updateDeps={UpdateDeps}", workspaceId, newBranchName, updateDependencies);

        var sink = new OperationErrorSink(workspaceId, logger, setRepositoryError, setLevelError);

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
            sink.Repository(repoId, msg);

        if (updateDependencies)
        {
            progress.Report("Updating dependencies...");
            var updateResult = await dependencyUpdateOrchestrator.RunAsync(
                workspaceId,
                cancellationToken,
                progress,
                setRepositoryError,
                setLevelError,
                onAppSideComplete: null,
                repoIdsToUpdate: null,
                commitMessage: commitMessage,
                includeDepsInCommitMessage: true);
            logger.LogInformation("NewFeatureOrchestrator completed for workspace {WorkspaceId}", workspaceId);
            return updateResult;
        }

        logger.LogInformation("NewFeatureOrchestrator completed for workspace {WorkspaceId}", workspaceId);
        return DependencyUpdateRunResult.Ok();
    }
}
