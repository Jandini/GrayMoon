using GrayMoon.App.Models;
using GrayMoon.Application.Features;

namespace GrayMoon.App.Services.Application;

public sealed class WorkspaceUpdateOperations(
    WorkspaceUpdateHandler updateHandler,
    WorkspaceGitService workspaceGitService) : IWorkspaceUpdateOperations
{
    public Task<(IReadOnlyList<SyncDependenciesRepoPayload> Payload, bool IsMultiLevel)> GetUpdatePlanAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        IReadOnlySet<int>? repositoryIds = null,
        CancellationToken cancellationToken = default)
        => workspaceGitService.GetUpdatePlanAsync(workspaceId, contextId, repositoryIds, cancellationToken);

    public Task<DependencyUpdateRunResult> UpdateAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
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
            contextId,
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
        WorkspaceFeatureContextId contextId,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
        => workspaceGitService.RestoreAllWorkspacePackagesAsync(workspaceId, contextId, progress.ToMessageAction(), cancellationToken);

    public Task<int> RestoreSyncedPackagesAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        IReadOnlySet<int> syncedRepoIds,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
        => workspaceGitService.RestoreSyncedWorkspacePackagesAsync(
            workspaceId,
            contextId,
            syncedRepoIds,
            progress.ToMessageAction(),
            cancellationToken);

    public Task UpdateSingleRepositoryAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        int repositoryId,
        Action<string>? onProgressMessage = null,
        Action<int, string>? onRepoError = null,
        CancellationToken cancellationToken = default)
        => workspaceGitService.RunUpdateSingleRepositoryAsync(
            workspaceId,
            contextId,
            repositoryId,
            onProgressMessage: onProgressMessage,
            onRepoError: onRepoError,
            cancellationToken: cancellationToken);

    public Task RecomputeAndBroadcastWorkspaceSyncedAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        CancellationToken cancellationToken = default)
        => workspaceGitService.RecomputeAndBroadcastWorkspaceSyncedAsync(workspaceId, contextId, cancellationToken);
}
