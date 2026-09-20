using GrayMoon.App.Models;
using GrayMoon.App.Services.Queries;

namespace GrayMoon.App.Services.Orchestration;

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
    /// Fetches every repository, checks that return-to-default is safe, then returns immediately with no options dialog.
    /// Any fetch, safety, or return-to-default failure aborts the rest of the batch. Intended for post-merge and future multi-PR callers
    /// that invoke this only after every merge in the batch succeeded.
    /// </summary>
    public async Task<UnattendedReturnToDefaultResult> ReturnToDefaultUnattendedAsync(
        int workspaceId,
        IReadOnlyList<int> repositoryIds,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(repositoryIds);

        var ids = repositoryIds.Distinct().ToList();
        if (ids.Count == 0)
            return new UnattendedReturnToDefaultResult(false, "No repositories to return to default.");

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
                    logger.LogError(ex, "Fetch failed before unattended return-to-default. WorkspaceId={WorkspaceId}, RepositoryId={RepositoryId}", workspaceId, repoId);
                    return new UnattendedReturnToDefaultResult(false, "Fetch failed. Return to default was aborted.");
                }

                if (!fetched)
                    return new UnattendedReturnToDefaultResult(false, "Fetch failed. Return to default was aborted.");

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
                logger.LogError(ex, "PR refresh failed before unattended return-to-default. WorkspaceId={WorkspaceId}", workspaceId);
                return new UnattendedReturnToDefaultResult(false, "Could not refresh pull request state. Return to default was aborted.");
            }

            var toSync = new List<(int RepoId, string BranchName, bool HasUpstream)>();
            foreach (var repoId in ids)
            {
                var dto = await query.GetSnapshotAsync(workspaceId, repoId, cancellationToken);
                if (dto == null)
                    return new UnattendedReturnToDefaultResult(false, "Repository state could not be read. Return to default was aborted.");

                if (!string.IsNullOrWhiteSpace(dto.CheckedOutTag))
                    return new UnattendedReturnToDefaultResult(false, "Repository is on a tag. Return to default was aborted.");

                var needsSync = !string.IsNullOrWhiteSpace(dto.BranchName)
                    && !string.Equals(dto.BranchName, dto.DefaultBranchName, StringComparison.Ordinal);
                if (!needsSync)
                    continue;

                var prMergedOrClosed = dto.PullRequestMergedAt.HasValue
                    || string.Equals(dto.PullRequestState, "closed", StringComparison.OrdinalIgnoreCase);
                if ((dto.DefaultBranchAheadCommits ?? 0) > 0 && !prMergedOrClosed)
                    return new UnattendedReturnToDefaultResult(false, "Return to default is not safe. Return to default was aborted.");

                toSync.Add((repoId, dto.BranchName!, dto.BranchHasUpstream == true));
            }

            if (toSync.Count == 0)
                return new UnattendedReturnToDefaultResult(true, null);

            progress.Report(toSync.Count == 1
                ? "Returning to default branch..."
                : $"Returning {toSync.Count} repositories to default branch...");

            var synced = 0;
            foreach (var (repoId, branchName, hasUpstream) in toSync)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var (success, errMsg) = await git.ReturnToDefaultDirectAsync(
                    workspaceId,
                    repoId,
                    branchName,
                    deleteRemoteBranch: hasUpstream,
                    allowForceDeleteLocalBranch: true,
                    cancellationToken);

                if (!success)
                {
                    await git.RecomputeAndBroadcastWorkspaceSyncedAsync(workspaceId, cancellationToken);
                    return new UnattendedReturnToDefaultResult(false, errMsg ?? "Return to default failed. Return to default was aborted.");
                }

                synced++;
                if (toSync.Count > 1)
                    progress.Report($"Returned {synced} of {toSync.Count} to default branch", synced, toSync.Count);
            }

            await git.RecomputeAndBroadcastWorkspaceSyncedAsync(workspaceId, cancellationToken);
            return new UnattendedReturnToDefaultResult(true, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unattended return-to-default failed. WorkspaceId={WorkspaceId}", workspaceId);
            return new UnattendedReturnToDefaultResult(false, "Return to default failed. Return to default was aborted.");
        }
    }
}

