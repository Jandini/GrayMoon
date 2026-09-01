using GrayMoon.App.Models;

namespace GrayMoon.Application;

public interface IWorkspaceUpdateOperations
{
    Task<(IReadOnlyList<SyncDependenciesRepoPayload> Payload, bool IsMultiLevel)> GetUpdatePlanAsync(
        int workspaceId,
        IReadOnlySet<int>? repositoryIds = null,
        CancellationToken cancellationToken = default);

    Task<DependencyUpdateRunResult> UpdateAsync(
        int workspaceId,
        CancellationToken cancellationToken,
        IProgress<OperationProgress>? progress,
        Action<int, string> setRepositoryError,
        IReadOnlySet<int>? repoIdsToUpdate = null,
        string? commitMessage = null,
        bool includeDepsInCommitMessage = true,
        int? maxLevel = null,
        string? runId = null);

    Task<int> RestorePackagesAsync(
        int workspaceId,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken);

    Task<int> RestoreSyncedPackagesAsync(
        int workspaceId,
        IReadOnlySet<int> syncedRepoIds,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken);
}
