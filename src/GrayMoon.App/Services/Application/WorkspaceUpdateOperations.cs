using GrayMoon.App.Models;

namespace GrayMoon.App.Services.Application;

public sealed class WorkspaceUpdateOperations(
    WorkspaceUpdateHandler updateHandler,
    WorkspaceGitService workspaceGitService) : IWorkspaceUpdateOperations
{
    public Task<(IReadOnlyList<SyncDependenciesRepoPayload> Payload, bool IsMultiLevel)> GetUpdatePlanAsync(
        int workspaceId,
        IReadOnlySet<int>? repositoryIds = null,
        CancellationToken cancellationToken = default)
        => workspaceGitService.GetUpdatePlanAsync(workspaceId, repositoryIds, cancellationToken);

    public Task<DependencyUpdateRunResult> UpdateAsync(
        int workspaceId,
        CancellationToken cancellationToken,
        IProgress<OperationProgress>? progress,
        Action<int, string> setRepositoryError,
        Action<int, string> setLevelError,
        IReadOnlySet<int>? repoIdsToUpdate = null,
        string? commitMessage = null,
        bool includeDepsInCommitMessage = true,
        int? maxLevel = null,
        string? runId = null)
        => updateHandler.RunUpdateAsync(
            workspaceId,
            cancellationToken,
            progress,
            setRepositoryError,
            setLevelError,
            repoIdsToUpdate: repoIdsToUpdate,
            commitMessage: commitMessage,
            includeDepsInCommitMessage: includeDepsInCommitMessage,
            maxLevel: maxLevel,
            runId: runId);

    public Task<int> RestorePackagesAsync(
        int workspaceId,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
        => workspaceGitService.RestoreAllWorkspacePackagesAsync(workspaceId, progress.ToMessageAction(), cancellationToken);

    public Task<int> RestoreSyncedPackagesAsync(
        int workspaceId,
        IReadOnlySet<int> syncedRepoIds,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
        => workspaceGitService.RestoreSyncedWorkspacePackagesAsync(
            workspaceId,
            syncedRepoIds,
            progress.ToMessageAction(),
            cancellationToken);
}
