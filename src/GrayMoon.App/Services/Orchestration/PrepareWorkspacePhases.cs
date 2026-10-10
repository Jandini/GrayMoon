using GrayMoon.Application.Features;

namespace GrayMoon.App.Services.Orchestration;

/// <summary>
/// The existing workflows <see cref="PrepareWorkspaceOrchestrator"/> sequences. Production runs them unchanged; tests
/// substitute them to check which phases run.
/// </summary>
public interface IPrepareWorkspacePhases
{
    Task<BranchCreationOutcome> CreateBranchesAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        string newBranchName,
        string baseBranch,
        IReadOnlySet<int>? repositoryIds,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken);

    Task<DependencyUpdateRunResult> UpdateDependenciesAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        string? commitMessage,
        IProgress<OperationProgress>? progress,
        Action<int, string> setRepositoryError,
        Action<int, string> setLevelError,
        CancellationToken cancellationToken);

    Task<UpdateAndPushResult> UpdateAndPushAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        string? commitMessage,
        Action<string> reportOverlay,
        Action<int, string> setRepositoryError,
        Action<int, string> setLevelError,
        CancellationToken cancellationToken);
}

/// <summary>
/// Branch creation with inline state sync (hooks suppressed), the dependency update, and the two-lane Update and Push,
/// each with the arguments Prepare Workspace has always used. The update and the pipeline take every repository
/// (<c>repoIdsToUpdate: null</c>): after the branch gate every targeted repository is on the new branch, and the
/// repositories deliberately left out (Skip Repos on Tags) are tag-pinned, which both the update and the push skip.
/// </summary>
public sealed class PrepareWorkspacePhases(
    WorkspaceBranchHandler branchHandler,
    DependencyUpdateOrchestrator dependencyUpdateOrchestrator,
    UpdateAndPushOrchestrator updateAndPushOrchestrator) : IPrepareWorkspacePhases
{
    public Task<BranchCreationOutcome> CreateBranchesAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        string newBranchName,
        string baseBranch,
        IReadOnlySet<int>? repositoryIds,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
        => branchHandler.CreateBranchesWithOutcomeAsync(
            workspaceId,
            contextId,
            newBranchName,
            baseBranch,
            repositoryIds,
            progress,
            syncState: true,
            cancellationToken);

    public Task<DependencyUpdateRunResult> UpdateDependenciesAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        string? commitMessage,
        IProgress<OperationProgress>? progress,
        Action<int, string> setRepositoryError,
        Action<int, string> setLevelError,
        CancellationToken cancellationToken)
        => dependencyUpdateOrchestrator.RunAsync(
            workspaceId,
            contextId,
            cancellationToken,
            progress,
            setRepositoryError,
            setLevelError,
            onAppSideComplete: null,
            repoIdsToUpdate: null,
            commitMessage: commitMessage,
            includeDepsInCommitMessage: true);

    public Task<UpdateAndPushResult> UpdateAndPushAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        string? commitMessage,
        Action<string> reportOverlay,
        Action<int, string> setRepositoryError,
        Action<int, string> setLevelError,
        CancellationToken cancellationToken)
        => updateAndPushOrchestrator.RunAsync(
            workspaceId,
            contextId,
            cancellationToken,
            reportOverlay,
            setRepositoryError,
            setLevelError,
            commitMessage,
            includeDepsInCommitMessage: true,
            restorePackages: true,
            runId: Guid.NewGuid().ToString("N")[..8]);
}
