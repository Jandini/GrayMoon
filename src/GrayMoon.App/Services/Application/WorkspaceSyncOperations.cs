using GrayMoon.App.Models;
using GrayMoon.App.Repositories;
using GrayMoon.Application.Features;

namespace GrayMoon.App.Services.Application;

public sealed class WorkspaceSyncOperations(
    WorkspaceSyncHandler syncHandler,
    WorkspaceCommitSyncHandler commitSyncHandler,
    WorkspaceUndoPushHandler undoPushHandler,
    WorkspaceRepository workspaceRepository,
    WorkspaceGitService workspaceGitService) : IWorkspaceSyncOperations
{
    public Task<IReadOnlyDictionary<int, RepoGitVersionInfo>> SyncAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        IReadOnlyList<int>? repositoryIds,
        bool skipDependencyLevelPersistence,
        CancellationToken cancellationToken,
        IProgress<OperationProgress>? progress,
        Action<int, RepoGitVersionInfo> updateRepoGitInfo,
        Action<int, RepoSyncStatus> setRepoSyncStatus)
        => syncHandler.RunSyncAsync(
            workspaceId,
            contextId,
            repositoryIds,
            skipDependencyLevelPersistence,
            cancellationToken,
            progress,
            updateRepoGitInfo,
            setRepoSyncStatus);

    public Task<ReturnToDefaultPlan> AnalyzeReturnToDefaultAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        IReadOnlyList<int> repositoryIds,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
        => syncHandler.AnalyzeReturnToDefaultAsync(workspaceId, contextId, repositoryIds, progress, cancellationToken);

    public Task<OperationResult> ExecuteReturnToDefaultAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        IReadOnlyList<int> repositoryIds,
        ReturnToDefaultOptions options,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
        => syncHandler.ExecuteReturnToDefaultAsync(workspaceId, contextId, repositoryIds, options, progress, cancellationToken);

    public Task<UnattendedReturnToDefaultResult> ReturnToDefaultAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        IReadOnlyList<int> repositoryIds,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
        => syncHandler.ReturnToDefaultUnattendedAsync(workspaceId, contextId, repositoryIds, progress, cancellationToken);

    public async Task<OperationResult> PullAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        int repositoryId,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        string? pageError = null;
        var repoErrors = new Dictionary<int, string>();
        await commitSyncHandler.CommitSyncAsync(
            workspaceId,
            contextId,
            repositoryId,
            cancellationToken,
            progress,
            (id, msg) =>
            {
                if (!string.IsNullOrWhiteSpace(msg))
                    repoErrors[id] = msg;
            },
            err => pageError = err);

        if (repoErrors.Count > 0)
            return OperationResult.Fail("Pull failed.", repoErrors);
        if (!string.IsNullOrWhiteSpace(pageError))
            return OperationResult.Fail(pageError);

        return OperationResult.Ok();
    }

    public async Task<OperationResult> PullLevelAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        IReadOnlyList<int> repositoryIds,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        string? pageError = null;
        var repoErrors = new Dictionary<int, string>();
        await commitSyncHandler.CommitSyncLevelAsync(
            workspaceId,
            contextId,
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

        if (repoErrors.Count > 0)
            return OperationResult.Fail("Pull failed.", repoErrors);
        if (!string.IsNullOrWhiteSpace(pageError))
            return OperationResult.Fail(pageError);

        return OperationResult.Ok();
    }

    public async Task<OperationResult> UndoPushAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        bool keepChanges,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        var workspace = await workspaceRepository.GetByIdAsync(workspaceId);
        if (workspace == null)
            return OperationResult.Fail("Workspace not found.");

        var results = await undoPushHandler.RunUndoPushAsync(
            workspaceId,
            contextId,
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

    public async Task<OperationResult> QuickFetchAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        IReadOnlyCollection<int>? repositoryIds,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        var errors = await workspaceGitService.QuickFetchAsync(
            workspaceId,
            contextId,
            repositoryIds,
            onProgress: (done, total) => progress.Report($"Fetched {done} of {total}", done, total),
            cancellationToken);
        return errors.Count == 0
            ? OperationResult.Ok()
            : OperationResult.Fail("Fetch failed.", errors);
    }
}
