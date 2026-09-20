using GrayMoon.App.Models;
using GrayMoon.Application.Features;

namespace GrayMoon.Application;

public interface IWorkspaceUpdateOperations
{
    Task<(IReadOnlyList<SyncDependenciesRepoPayload> Payload, bool IsMultiLevel)> GetUpdatePlanAsync(
        int workspaceId,
        IReadOnlySet<int>? repositoryIds = null,
        CancellationToken cancellationToken = default);

    Task<DependencyUpdateRunResult> UpdateAsync(
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
        string? runId = null);

    Task<int> RestorePackagesAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken);

    Task<int> RestoreSyncedPackagesAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        IReadOnlySet<int> syncedRepoIds,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken);

    /// <summary>Update dependencies for a single repository only (refresh projects, sync deps, no commit).</summary>
    Task UpdateSingleRepositoryAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        int repositoryId,
        Action<string>? onProgressMessage = null,
        Action<int, string>? onRepoError = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Closes out a user action: recomputes workspace-wide file-version and dependency stats, then
    /// broadcasts WorkspaceSynced once so the grid refreshes. Call exactly once per action, after every
    /// repository in the batch has been written.
    /// </summary>
    Task RecomputeAndBroadcastWorkspaceSyncedAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        CancellationToken cancellationToken = default);
}
