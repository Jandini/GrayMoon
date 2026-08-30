using GrayMoon.App.Models;

namespace GrayMoon.Application;

public sealed record UnattendedSyncToDefaultResult(bool Completed, string? AbortReason);

public interface IWorkspaceSyncOperations
{
    Task<IReadOnlyDictionary<int, RepoGitVersionInfo>> SyncAsync(
        int workspaceId,
        IReadOnlyList<int>? repositoryIds,
        bool skipDependencyLevelPersistence,
        CancellationToken cancellationToken,
        IProgress<OperationProgress>? progress,
        Action<int, RepoGitVersionInfo> updateRepoGitInfo,
        Action<int, RepoSyncStatus> setRepoSyncStatus);

    Task<UnattendedSyncToDefaultResult> SyncToDefaultAsync(
        int workspaceId,
        IReadOnlyList<int> repositoryIds,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken);

    Task<OperationResult> PullAsync(
        int workspaceId,
        int repositoryId,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken);

    Task<OperationResult> PullLevelAsync(
        int workspaceId,
        IReadOnlyList<int> repositoryIds,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken);

    Task<OperationResult> UndoPushAsync(
        int workspaceId,
        bool keepChanges,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken);
}
