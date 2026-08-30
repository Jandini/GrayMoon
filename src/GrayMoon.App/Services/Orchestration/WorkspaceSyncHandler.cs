using GrayMoon.App.Models;
using GrayMoon.App.Services.Queries;

namespace GrayMoon.App.Services.Orchestration;

/// <summary>Outcome of an unattended sync-to-default. <see cref="Completed"/> is false when fetch, safety, or sync aborted the batch.</summary>
public sealed record UnattendedSyncToDefaultResult(bool Completed, string? AbortReason);

/// <summary>
/// Handles sync operations (git status, version, branch, commit counts) for workspace repositories.
/// Stateless; all state is provided via callbacks.
/// </summary>
public sealed class WorkspaceSyncHandler(ILogger<WorkspaceSyncHandler> logger, IServiceScopeFactory serviceScopeFactory)
{
    public async Task<IReadOnlyDictionary<int, RepoGitVersionInfo>> RunSyncAsync(
        int workspaceId,
        IReadOnlyList<int>? repositoryIds,
        bool skipDependencyLevelPersistence,
        CancellationToken cancellationToken,
        IProgress<OperationProgress>? progress,
        Action<int, RepoGitVersionInfo> updateRepoGitInfo,
        Action<int, RepoSyncStatus> setRepoSyncStatus,
        Action? onAppSideComplete = null)
    {
        await using var scope = serviceScopeFactory.CreateAsyncScope();
        var workspaceGitService = scope.ServiceProvider.GetRequiredService<WorkspaceGitService>();

        try
        {
            var results = await workspaceGitService.SyncAsync(
                workspaceId,
                onProgress: (completed, total, repoId, info) =>
                {
                    progress.Report($"Synchronized {completed} of {total}", completed, total);
                    var status = !string.IsNullOrWhiteSpace(info.ErrorMessage) || info.Version != "-" || info.Branch != "-"
                        ? RepoSyncStatus.InSync
                        : RepoSyncStatus.Error;
                    setRepoSyncStatus(repoId, status);
                    updateRepoGitInfo(repoId, info);
                },
                onAppSideComplete: onAppSideComplete,
                repositoryIds: repositoryIds,
                skipDependencyLevelPersistence: skipDependencyLevelPersistence,
                cancellationToken: cancellationToken);

            return results;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Error running workspace sync for WorkspaceId={WorkspaceId}", workspaceId);
            throw;
        }
    }

    /// <summary>
    /// Fetches every repository, checks that sync-to-default is safe, then syncs immediately with no options dialog.
    /// Any fetch, safety, or sync failure aborts the rest of the batch. Intended for post-merge and future multi-PR callers
    /// that invoke this only after every merge in the batch succeeded.
    /// </summary>
    public async Task<UnattendedSyncToDefaultResult> SyncToDefaultUnattendedAsync(
        int workspaceId,
        IReadOnlyList<int> repositoryIds,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(repositoryIds);

        var ids = repositoryIds.Distinct().ToList();
        if (ids.Count == 0)
            return new UnattendedSyncToDefaultResult(false, "No repositories to sync.");

        await using var scope = serviceScopeFactory.CreateAsyncScope();
        var git = scope.ServiceProvider.GetRequiredService<WorkspaceGitService>();
        var prService = scope.ServiceProvider.GetRequiredService<WorkspacePullRequestService>();
        var query = scope.ServiceProvider.GetRequiredService<IWorkspaceRepositoryLinkListQueryService>();

        try
        {
            progress.Report(ids.Count == 1
                ? "Fetching latest branch state..."
                : $"Fetching latest branch state for {ids.Count} repositories...");

            var fetchDone = 0;
            foreach (var repoId in ids)
            {
                cancellationToken.ThrowIfCancellationRequested();
                bool fetched;
                try
                {
                    fetched = await git.RefreshBranchesForRepositoryAsync(repoId, workspaceId, cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Fetch failed before unattended sync-to-default. WorkspaceId={WorkspaceId}, RepositoryId={RepositoryId}", workspaceId, repoId);
                    return new UnattendedSyncToDefaultResult(false, "Fetch failed. Sync to default was aborted.");
                }

                if (!fetched)
                    return new UnattendedSyncToDefaultResult(false, "Fetch failed. Sync to default was aborted.");

                fetchDone++;
                if (ids.Count > 1)
                    progress.Report($"Fetched {fetchDone} of {ids.Count}...", fetchDone, ids.Count);
            }

            try
            {
                await prService.RefreshPullRequestsAsync(workspaceId, ids, force: true, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "PR refresh failed before unattended sync-to-default. WorkspaceId={WorkspaceId}", workspaceId);
                return new UnattendedSyncToDefaultResult(false, "Could not refresh pull request state. Sync to default was aborted.");
            }

            var toSync = new List<(int RepoId, string BranchName, bool HasUpstream)>();
            foreach (var repoId in ids)
            {
                var dto = await query.GetSnapshotAsync(workspaceId, repoId, cancellationToken);
                if (dto == null)
                    return new UnattendedSyncToDefaultResult(false, "Repository state could not be read. Sync to default was aborted.");

                if (!string.IsNullOrWhiteSpace(dto.CheckedOutTag))
                    return new UnattendedSyncToDefaultResult(false, "Repository is on a tag. Sync to default was aborted.");

                var needsSync = !string.IsNullOrWhiteSpace(dto.BranchName)
                    && !string.Equals(dto.BranchName, dto.DefaultBranchName, StringComparison.Ordinal);
                if (!needsSync)
                    continue;

                var prMergedOrClosed = dto.PullRequestMergedAt.HasValue
                    || string.Equals(dto.PullRequestState, "closed", StringComparison.OrdinalIgnoreCase);
                if ((dto.DefaultBranchAheadCommits ?? 0) > 0 && !prMergedOrClosed)
                    return new UnattendedSyncToDefaultResult(false, "Sync to default is not safe. Sync to default was aborted.");

                toSync.Add((repoId, dto.BranchName!, dto.BranchHasUpstream == true));
            }

            if (toSync.Count == 0)
                return new UnattendedSyncToDefaultResult(true, null);

            progress.Report(toSync.Count == 1
                ? "Synchronizing to default branch..."
                : $"Synchronizing {toSync.Count} repositories to default branch...");

            var synced = 0;
            foreach (var (repoId, branchName, hasUpstream) in toSync)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var (success, errMsg) = await git.SyncToDefaultDirectAsync(
                    workspaceId,
                    repoId,
                    branchName,
                    deleteRemoteBranch: hasUpstream,
                    allowForceDeleteLocalBranch: true,
                    cancellationToken);

                if (!success)
                {
                    await git.RecomputeAndBroadcastWorkspaceSyncedAsync(workspaceId, cancellationToken);
                    return new UnattendedSyncToDefaultResult(false, errMsg ?? "Sync to default failed. Sync to default was aborted.");
                }

                synced++;
                if (toSync.Count > 1)
                    progress.Report($"Synchronized {synced} of {toSync.Count} to default branch", synced, toSync.Count);
            }

            await git.RecomputeAndBroadcastWorkspaceSyncedAsync(workspaceId, cancellationToken);
            return new UnattendedSyncToDefaultResult(true, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unattended sync-to-default failed. WorkspaceId={WorkspaceId}", workspaceId);
            return new UnattendedSyncToDefaultResult(false, "Sync to default failed. Sync to default was aborted.");
        }
    }
}

