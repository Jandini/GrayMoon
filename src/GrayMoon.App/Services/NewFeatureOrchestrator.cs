namespace GrayMoon.App.Services;

/// <summary>
/// Sequences the New Feature workflow: parallel branch creation with inline state sync (hooks
/// suppressed), then dependency update. By the time each phase completes, the database reflects
/// full, consistent state — no async hook syncs to race against.
/// </summary>
public sealed class NewFeatureOrchestrator(
    WorkspaceBranchHandler branchHandler,
    DependencyUpdateOrchestrator dependencyUpdateOrchestrator,
    ILogger<NewFeatureOrchestrator> logger)
{
    public async Task RunAsync(
        int workspaceId,
        string newBranchName,
        string baseBranch,
        IReadOnlySet<int>? repositoryIds,
        bool updateDependencies,
        string? commitMessage,
        Action<string> setProgress,
        Action<int, int> reportBranchProgress,
        Action<int, string> setRepositoryError,
        CancellationToken cancellationToken)
    {
        logger.LogInformation("NewFeatureOrchestrator starting for workspace {WorkspaceId}: branch={Branch}, updateDeps={UpdateDeps}", workspaceId, newBranchName, updateDependencies);

        setProgress("Creating branches...");
        await branchHandler.CreateBranchesAsync(
            workspaceId,
            newBranchName,
            baseBranch,
            repositoryIds,
            reportBranchProgress,
            syncState: true,
            cancellationToken);

        if (updateDependencies)
        {
            setProgress("Updating dependencies...");
            await dependencyUpdateOrchestrator.RunAsync(
                workspaceId,
                cancellationToken,
                setProgress,
                setRepositoryError,
                onAppSideComplete: null,
                repoIdsToUpdate: null,
                commitMessage: commitMessage,
                includeDepsInCommitMessage: true);
        }

        logger.LogInformation("NewFeatureOrchestrator completed for workspace {WorkspaceId}", workspaceId);
    }
}
