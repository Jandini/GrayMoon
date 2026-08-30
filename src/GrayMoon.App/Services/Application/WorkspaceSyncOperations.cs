using GrayMoon.App.Models;
using GrayMoon.App.Repositories;
using GrayMoon.App.Services.Queries;

namespace GrayMoon.App.Services.Application;

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

public sealed class WorkspaceSyncOperations(
    WorkspaceSyncHandler syncHandler,
    WorkspaceCommitSyncHandler commitSyncHandler,
    WorkspaceUndoPushHandler undoPushHandler,
    WorkspaceRepository workspaceRepository) : IWorkspaceSyncOperations
{
    public Task<IReadOnlyDictionary<int, RepoGitVersionInfo>> SyncAsync(
        int workspaceId,
        IReadOnlyList<int>? repositoryIds,
        bool skipDependencyLevelPersistence,
        CancellationToken cancellationToken,
        IProgress<OperationProgress>? progress,
        Action<int, RepoGitVersionInfo> updateRepoGitInfo,
        Action<int, RepoSyncStatus> setRepoSyncStatus)
        => syncHandler.RunSyncAsync(
            workspaceId,
            repositoryIds,
            skipDependencyLevelPersistence,
            cancellationToken,
            progress,
            updateRepoGitInfo,
            setRepoSyncStatus);

    public Task<UnattendedSyncToDefaultResult> SyncToDefaultAsync(
        int workspaceId,
        IReadOnlyList<int> repositoryIds,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
        => syncHandler.SyncToDefaultUnattendedAsync(workspaceId, repositoryIds, progress, cancellationToken);

    public async Task<OperationResult> PullAsync(
        int workspaceId,
        int repositoryId,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        string? pageError = null;
        var repoErrors = new Dictionary<int, string>();
        await commitSyncHandler.CommitSyncAsync(
            workspaceId,
            repositoryId,
            cancellationToken,
            progress,
            (id, msg) =>
            {
                if (!string.IsNullOrWhiteSpace(msg))
                    repoErrors[id] = msg;
            },
            err => pageError = err);

        if (!string.IsNullOrWhiteSpace(pageError) || repoErrors.Count > 0)
            return OperationResult.Fail(pageError ?? "Pull failed.", repoErrors);

        return OperationResult.Ok();
    }

    public async Task<OperationResult> PullLevelAsync(
        int workspaceId,
        IReadOnlyList<int> repositoryIds,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        string? pageError = null;
        var repoErrors = new Dictionary<int, string>();
        await commitSyncHandler.CommitSyncLevelAsync(
            workspaceId,
            repositoryIds,
            cancellationToken,
            (completed, total) =>
            {
                progress.Report($"Pulled {completed} of {total}", completed, total);
                return Task.CompletedTask;
            },
            (id, msg) =>
            {
                if (!string.IsNullOrWhiteSpace(msg))
                    repoErrors[id] = msg;
            },
            err => pageError = err);

        if (!string.IsNullOrWhiteSpace(pageError) || repoErrors.Count > 0)
            return OperationResult.Fail(pageError ?? "Pull failed.", repoErrors);

        return OperationResult.Ok();
    }

    public async Task<OperationResult> UndoPushAsync(
        int workspaceId,
        bool keepChanges,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        var workspace = await workspaceRepository.GetByIdAsync(workspaceId);
        if (workspace == null)
            return OperationResult.Fail("Workspace not found.");

        var results = await undoPushHandler.RunUndoPushAsync(
            workspaceId,
            workspace.Repositories.ToList(),
            keepChanges,
            progress,
            cancellationToken);

        var errors = results
            .Where(r => !r.Success && !string.IsNullOrWhiteSpace(r.Error))
            .ToDictionary(r => r.RepositoryId, r => r.Error!);

        return errors.Count == 0
            ? OperationResult.Ok()
            : OperationResult.Fail("Undo push failed for one or more repositories.", errors);
    }
}
