using GrayMoon.App.Models;
using GrayMoon.App.Services;

namespace GrayMoon.App.Components.Pages;

public sealed partial class WorkspaceRepositories
{
    private IReadOnlyList<SyncToDefaultCheckResult>? _syncToDefaultCheckResults = null;

    private sealed record SyncToDefaultCheckResult(int RepoId, int? DefaultAhead, bool? HasUpstream);

    private async Task ShowConfirmSyncToDefaultLevel(List<int> repositoryIds)
    {
        if (workspace == null || repositoryIds == null || repositoryIds.Count == 0)
            return;

        var freshLinks = await GetFreshLinkStatesAsync(repositoryIds.Distinct().ToList());
        var nonDefaultRepoIds = freshLinks.Values
            .Where(s => s.NeedsSyncToDefault)
            .Select(s => s.Link.RepositoryId)
            .ToList();

        if (nonDefaultRepoIds.Count == 0)
        {
            ToastService.Show("All repositories in this level are already on the default branch.");
            return;
        }

        await CheckBranchesAndConfirmSyncToDefaultLevel(nonDefaultRepoIds);
    }

    private async Task CheckBranchesAndConfirmSyncToDefaultLevel(List<int> repositoryIds)
    {
        if (workspace == null || repositoryIds == null || repositoryIds.Count == 0 || IsJobRunning)
            return;

        _syncToDefaultCheckResults = null;

        try
        {
            await WorkspacePageService.WorkspacePullRequestService.RefreshPullRequestsAsync(WorkspaceId, repositoryIds, force: true);
            await ReloadWorkspaceDataFromFreshScopeAsync();
            ApplySyncStateFromLoadedItems();
            await InvokeAsync(StateHasChanged);
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "PR refresh before sync-to-default check failed for workspace {WorkspaceId}", WorkspaceId);
        }

        // Pre-check against the state just persisted by the PR refresh above; no agent call needed.
        var freshLinks = await GetFreshLinkStatesAsync(repositoryIds);
        var checkResults = repositoryIds
            .Where(freshLinks.ContainsKey)
            .Select(repoId =>
            {
                var state = freshLinks[repoId];
                return new SyncToDefaultCheckResult(repoId, state.Link.DefaultBranchAheadCommits, state.Link.BranchHasUpstream);
            })
            .ToList();
        var safeRepoIds = checkResults
            .Where(r => (r.DefaultAhead ?? 0) == 0 || freshLinks[r.RepoId].IsPullRequestMergedOrClosed)
            .Select(r => r.RepoId)
            .ToList();
        var blocked = checkResults
            .Where(r => (r.DefaultAhead ?? 0) > 0 && !freshLinks[r.RepoId].IsPullRequestMergedOrClosed)
            .ToList();

        foreach (var r in blocked)
        {
            var name = freshLinks[r.RepoId].Link.Repository?.RepositoryName ?? r.RepoId.ToString();
            ToastService.Show($"{name}: skipped sync to default (commits ahead of default, PR not merged).");
        }

        if (safeRepoIds.Count == 0)
        {
            if (blocked.Count == 0)
                ToastService.Show("No repositories to sync.");
            return;
        }

        _syncToDefaultCheckResults = checkResults.Where(r => safeRepoIds.Contains(r.RepoId)).ToList();
        var safeCount = safeRepoIds.Count;
        var dialogMessage = safeCount == 1
            ? "This will checkout the default branch, remove the current branch locally, and pull the latest. Uncommitted local changes can block checkout."
            : $"This will sync {safeCount} repositories to their default branch: checkout default, remove the current branch locally, and pull. Uncommitted local changes can block checkout for that repo.";

        JobService.StartJob(PageJobKey,
            safeCount == 1 ? "Fetching latest branch state..." : $"Fetching latest branch state for {safeCount} repositories...",
            async (job, ct) =>
            {
                try
                {
                    var fetchDone = 0;
                    using var fetchSemaphore = new System.Threading.SemaphoreSlim(8);
                    await Task.WhenAll(safeRepoIds.Select(async repoId =>
                    {
                        await fetchSemaphore.WaitAsync(ct);
                        try
                        {
                            await ScopedExecutor.ExecuteAsync<WorkspaceGitService>(
                                svc => svc.RefreshBranchesForRepositoryAsync(repoId, WorkspaceId, ct));
                        }
                        finally
                        {
                            fetchSemaphore.Release();
                            var c = Interlocked.Increment(ref fetchDone);
                            job.ReportProgress($"Fetched {c} of {safeRepoIds.Count}...");
                        }
                    }));

                    // The fetches above rewrote BranchHasUpstream, so rebuild the dialog from the database
                    // rather than from whatever the grid cache still holds.
                    var refreshed = await GetFreshLinkStatesAsync(safeRepoIds);
                    var updatedResults = _syncToDefaultCheckResults?
                        .Select(r => new SyncToDefaultCheckResult(
                            r.RepoId,
                            r.DefaultAhead,
                            refreshed.TryGetValue(r.RepoId, out var s) ? s.Link.BranchHasUpstream : r.HasUpstream))
                        .ToList();

                    await InvokeAsync(async () =>
                    {
                        if (_disposed) return;
                        await RefreshFromSync();

                        _syncToDefaultCheckResults = updatedResults;
                        var repoItems = updatedResults?
                            .Select(r =>
                            {
                                refreshed.TryGetValue(r.RepoId, out var s);
                                return new SyncToDefaultRepoItem(
                                    s?.Link.Repository?.RepositoryName ?? r.RepoId.ToString(),
                                    s?.Link.BranchName ?? "",
                                    r.HasUpstream == true,
                                    PrState: null,
                                    CommitsAhead: 0);
                            })
                            .ToList() ?? new List<SyncToDefaultRepoItem>();
                        ShowSyncToDefaultOptions(dialogMessage, repoItems, (deleteRemote, allowForce) => SyncToDefaultLevelAsync(safeRepoIds, deleteRemote, allowForce));
                        StateHasChanged();
                    });
                }
                catch (OperationCanceledException)
                {
                    SafeInvoke(() => ToastService.Show("Fetch cancelled."));
                    throw;
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "Error checking branches for sync to default");
                    SafeInvoke(() => ToastService.ShowError("Failed to prepare sync to default."));
                    throw;
                }
            });
    }

    private async Task SyncToDefaultFromModalAsync((int RepositoryId, string? RepositoryName, string CurrentBranchName, string DefaultBranch) request)
    {
        var (repositoryId, repositoryName, currentBranchName, defaultBranch) = request;
        if (workspace == null || IsJobRunning)
            return;
        if (string.IsNullOrWhiteSpace(repositoryName))
            return;
        if (IsRepoOnTag(repositoryId))
        {
            ToastService.Show(TagBlockedActionMessage);
            CloseSwitchBranchModal();
            return;
        }

        CloseSwitchBranchModal();

        try
        {
            // Use persisted workspace link state (updated by hooks); no agent GetCommitCounts call.
            var states = await GetFreshLinkStatesAsync([repositoryId]);
            states.TryGetValue(repositoryId, out var state);
            var defaultAhead = state?.Link.DefaultBranchAheadCommits ?? 0;
            var hasUpstream = state?.Link.BranchHasUpstream == true;

            if (defaultAhead > 0)
            {
                try
                {
                    await WorkspacePageService.WorkspacePullRequestService.RefreshPullRequestsAsync(WorkspaceId, new[] { repositoryId }, force: true);
                    states = await GetFreshLinkStatesAsync([repositoryId]);
                    states.TryGetValue(repositoryId, out state);
                    await ReloadWorkspaceDataFromFreshScopeAsync();
                    ApplySyncStateFromLoadedItems();
                    await InvokeAsync(StateHasChanged);
                }
                catch (Exception ex)
                {
                    Logger.LogDebug(ex, "PR refresh before sync-to-default check failed for RepositoryId={RepositoryId}", repositoryId);
                }
            }

            if (defaultAhead > 0 && state?.IsPullRequestMergedOrClosed != true)
            {
                ToastService.Show("Skipped sync to default: commits ahead of default branch and PR is not merged.");
                return;
            }

            // The dialog is what asks for permission to delete the local branch, so it is also needed when
            // there is no remote branch to delete but the branch still carries commits the default branch
            // does not have.
            if (hasUpstream || defaultAhead > 0)
            {
                var branchName = state?.Link.BranchName ?? currentBranchName;
                var singlePr = state?.PullRequest;
                var singlePrState = singlePr == null ? null : singlePr.IsMerged ? "merged" : singlePr.IsClosed ? "closed" : "open";
                ShowSyncToDefaultOptions(
                    "This will checkout the default branch, remove the current branch locally, and pull the latest.",
                    [new SyncToDefaultRepoItem(repositoryName!, branchName, hasUpstream, singlePrState, defaultAhead)],
                    (deleteRemote, allowForce) => SyncToDefaultSingleRepoAfterCheckAsync(repositoryId, repositoryName, currentBranchName, deleteRemote && hasUpstream, defaultBranch, allowForce));
            }
            else
            {
                await SyncToDefaultSingleRepoAfterCheckAsync(repositoryId, repositoryName, currentBranchName, deleteRemoteBranch: false, defaultBranch, allowForceDeleteLocalBranch: true);
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error preparing sync to default (repository {RepositoryId})", repositoryId);
            ToastService.ShowError("Failed to prepare sync to default.");
        }
    }

    private Task SyncToDefaultSingleRepoAfterCheckAsync(int repositoryId, string repositoryName, string currentBranchName, bool deleteRemoteBranch = false, string? defaultBranchName = null, bool allowForceDeleteLocalBranch = true)
    {
        if (workspace == null || IsJobRunning)
            return Task.CompletedTask;

        var message = string.IsNullOrWhiteSpace(defaultBranchName)
            ? "Synchronizing to default branch..."
            : $"Synchronizing to {defaultBranchName}...";

        StartPageJob(message, async (job, ct) =>
        {
            var (success, errMsg) = await ScopedExecutor.ExecuteAsync<WorkspaceGitService, (bool Success, string? ErrorMessage)>(
                svc => svc.SyncToDefaultDirectAsync(WorkspaceId, repositoryId, currentBranchName, deleteRemoteBranch, allowForceDeleteLocalBranch, ct));

            // The sync persists this repository's own state; workspace-wide dependency and file-version
            // stats are recomputed here, once, as the batch boundary for this action.
            await ScopedExecutor.ExecuteAsync<WorkspaceGitService>(
                svc => svc.RecomputeAndBroadcastWorkspaceSyncedAsync(WorkspaceId, ct));

            if (success)
            {
                SafeInvoke(() => repositoryErrors.Remove(repositoryId));
                await InvokeAsync(async () => { if (_disposed) return; await RefreshFromSync(); });
            }
            else if (errMsg != null)
            {
                SafeInvoke(() => SetRepositoryError(repositoryId, errMsg));
            }
        }, new PageJobOptions
        {
            RefreshOnSuccess = false,
            OnError = ex =>
            {
                Logger.LogError(ex, "Error syncing to default branch for repository {RepositoryId}", repositoryId);
                SafeInvoke(() => SetRepositoryError(repositoryId, "An error occurred while syncing to default branch. The GrayMoon Agent may be offline."));
            }
        });

        return Task.CompletedTask;
    }

    private Task SyncToDefaultLevelAsync(List<int> repositoryIds, bool deleteRemoteBranch = false, bool allowForceDeleteLocalBranch = true)
    {
        if (workspace == null || repositoryIds == null || repositoryIds.Count == 0 || IsJobRunning)
            return Task.CompletedTask;

        repositoryIds = repositoryIds.Where(id => !IsRepoOnTag(id)).ToList();
        if (repositoryIds.Count == 0)
        {
            ToastService.Show("All repositories are on tags; checkout a branch first.");
            return Task.CompletedTask;
        }

        var checkResults = _syncToDefaultCheckResults;
        _syncToDefaultCheckResults = null;
        errorMessage = null;

        StartPageJob("Synchronizing to default branch...", async (job, ct) =>
        {
            var total = repositoryIds.Count;
            var maxParallel = Math.Max(1, WorkspaceOptions?.Value?.MaxParallelOperations ?? 16);
            var resultByRepo = checkResults?.ToDictionary(r => r.RepoId) ?? new Dictionary<int, SyncToDefaultCheckResult>();
            var completedCount = 0;

            using var semaphore = new SemaphoreSlim(maxParallel, maxParallel);

            var linkStates = await GetFreshLinkStatesAsync(repositoryIds);

            var tasks = repositoryIds.Select(async repositoryId =>
            {
                linkStates.TryGetValue(repositoryId, out var linkState);
                var currentBranchName = linkState?.Link.BranchName;
                if (string.IsNullOrWhiteSpace(currentBranchName))
                {
                    var c = Interlocked.Increment(ref completedCount);
                    if (total > 1)
                        job.ReportProgress($"Synchronized {c} of {total} to default branch");
                    return (repositoryId, true, (string?)null);
                }

                await semaphore.WaitAsync(ct);
                try
                {
                    var repoHasRemote = !resultByRepo.TryGetValue(repositoryId, out var repoCheck) || repoCheck.HasUpstream == true;
                    var (success, errMsg) = await ScopedExecutor.ExecuteAsync<WorkspaceGitService, (bool Success, string? ErrorMessage)>(
                        svc => svc.SyncToDefaultDirectAsync(
                            WorkspaceId, repositoryId, currentBranchName,
                            deleteRemoteBranch && repoHasRemote, allowForceDeleteLocalBranch, ct));

                    return (repositoryId, success, errMsg);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Logger.LogError(ex, "Error syncing to default branch for repository {RepositoryId}", repositoryId);
                    return (repositoryId, false, (string?)"Sync to default branch failed. The GrayMoon Agent may be offline.");
                }
                finally
                {
                    semaphore.Release();
                    var c = Interlocked.Increment(ref completedCount);
                    if (total > 1)
                        job.ReportProgress($"Synchronized {c} of {total} to default branch");
                }
            });

            var results = await Task.WhenAll(tasks);

            // Each repository persisted its own git/project state above; workspace-wide dependency and
            // file-version stats are recomputed once here, after every repository in the level has finished,
            // so the recompute reads a complete snapshot instead of racing N concurrent whole-workspace
            // read-then-overwrite passes.
            await ScopedExecutor.ExecuteAsync<WorkspaceGitService>(
                svc => svc.RecomputeAndBroadcastWorkspaceSyncedAsync(WorkspaceId, ct));

            SafeInvoke(() =>
            {
                foreach (var (repoId, success, errMsg) in results)
                {
                    if (success)
                    {
                        repositoryErrors.Remove(repoId);
                    }
                    else if (errMsg != null)
                    {
                        SetRepositoryError(repoId, errMsg);
                    }
                }
            });
        }, new PageJobOptions
        {
            OnError = ex =>
            {
                Logger.LogError(ex, "Error syncing to default branch for level");
                SafeInvoke(() => errorMessage = "An error occurred while syncing to default branch. The GrayMoon Agent may be offline.");
            }
        });

        return Task.CompletedTask;
    }

    private async Task SyncAllToDefaultAsync()
    {
        if (workspace == null || IsJobRunning)
            return;

        var allLinks = await GetAllLinksForOperationAsync();
        var eligibleRepos = allLinks
            .Where(wr =>
                !wr.IsOnTag &&
                !string.IsNullOrWhiteSpace(wr.BranchName) &&
                !string.IsNullOrWhiteSpace(wr.DefaultBranchName) &&
                !string.Equals(wr.BranchName, wr.DefaultBranchName, StringComparison.Ordinal))
            .ToList();

        if (eligibleRepos.Count == 0)
        {
            ToastService.Show("All repositories are already on the default branch.");
            return;
        }

        var eligibleIds = eligibleRepos.Select(wr => wr.RepositoryId).ToList();

        var totalCount = eligibleIds.Count;
        var dialogMessage = totalCount == 1
            ? "This will checkout the default branch, remove the current branch locally, and pull the latest. Uncommitted local changes can block checkout."
            : $"This will sync {totalCount} repositories to their default branch: checkout default, remove the current branch locally, and pull. Uncommitted local changes can block checkout for that repo.";

        JobService.StartJob(PageJobKey,
            totalCount == 1 ? "Fetching latest branch state..." : $"Fetching latest branch state for {totalCount} repositories...",
            async (job, ct) =>
            {
                try
                {
                    try
                    {
                        await ScopedExecutor.ExecuteAsync<WorkspacePullRequestService>(
                            svc => svc.RefreshPullRequestsAsync(WorkspaceId, eligibleIds, force: true, ct));
                    }
                    catch (Exception ex)
                    {
                        Logger.LogDebug(ex, "PR refresh before sync-all-to-default failed for workspace {WorkspaceId}", WorkspaceId);
                    }

                    var fetchDone = 0;
                    using var fetchSemaphore = new SemaphoreSlim(8);
                    await Task.WhenAll(eligibleIds.Select(async repoId =>
                    {
                        await fetchSemaphore.WaitAsync(ct);
                        try
                        {
                            await ScopedExecutor.ExecuteAsync<WorkspaceGitService>(
                                svc => svc.RefreshBranchesForRepositoryAsync(repoId, WorkspaceId, ct));
                        }
                        finally
                        {
                            fetchSemaphore.Release();
                            var c = Interlocked.Increment(ref fetchDone);
                            job.ReportProgress($"Fetched {c} of {totalCount}...");
                        }
                    }));

                    // Both the PR refresh and the branch fetches above wrote to the database, so build the
                    // dialog from it rather than from the grid cache the page last rendered.
                    var refreshed = await GetFreshLinkStatesAsync(eligibleIds);
                    var repoItems = eligibleIds
                        .Select(repoId =>
                        {
                            refreshed.TryGetValue(repoId, out var s);
                            var pr = s?.PullRequest;
                            var prState = pr == null ? null : pr.IsMerged ? "merged" : pr.IsClosed ? "closed" : "open";
                            return new SyncToDefaultRepoItem(
                                s?.Link.Repository?.RepositoryName ?? repoId.ToString(),
                                s?.Link.BranchName ?? "",
                                s?.Link.BranchHasUpstream == true,
                                prState,
                                s?.Link.DefaultBranchAheadCommits ?? 0);
                        })
                        .ToList();

                    await InvokeAsync(async () =>
                    {
                        if (_disposed) return;
                        await RefreshFromSync();
                        ShowSyncToDefaultOptions(dialogMessage, repoItems, (deleteRemote, allowForce) => ExecuteSyncAllToDefaultAsync(repoItems, deleteRemote, allowForce));
                        StateHasChanged();
                    });
                }
                catch (OperationCanceledException)
                {
                    SafeInvoke(() => ToastService.Show("Fetch cancelled."));
                    throw;
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "Error fetching branch state before sync all to default");
                    SafeInvoke(() => ToastService.ShowError("Failed to prepare sync to default."));
                    throw;
                }
            });
    }

    private async Task ExecuteSyncAllToDefaultAsync(
        IReadOnlyList<SyncToDefaultRepoItem> repoItems,
        bool deleteRemoteBranch,
        bool allowForceDeleteLocalBranch)
    {
        if (workspace == null || repoItems.Count == 0 || IsJobRunning)
            return;

        var allLinks = await GetAllLinksForOperationAsync();
        var repoIdByName = allLinks.ToDictionary(
            wr => wr.Repository?.RepositoryName ?? string.Empty,
            wr => wr.RepositoryId);
        var openPrRepoIds = repoItems
            .Where(r => r.PrState == "open" && repoIdByName.ContainsKey(r.RepoName))
            .Select(r => repoIdByName[r.RepoName])
            .ToList();
        var openPrStates = await GetFreshLinkStatesAsync(openPrRepoIds);
        var prNumberByRepoName = new Dictionary<string, int>();
        foreach (var item in repoItems.Where(r => r.PrState == "open"))
        {
            if (!repoIdByName.TryGetValue(item.RepoName, out var repoId)) continue;
            if (openPrStates.TryGetValue(repoId, out var s) && s.PullRequest is { Number: > 0 } pr)
                prNumberByRepoName[item.RepoName] = pr.Number;
        }

        var total = repoItems.Count;
        errorMessage = null;

        StartPageJob("Synchronizing to default branch...", async (job, ct) =>
        {
            var maxParallel = Math.Max(1, WorkspaceOptions?.Value?.MaxParallelOperations ?? 16);
            var completedCount = 0;

            using var semaphore = new SemaphoreSlim(maxParallel, maxParallel);

            var linkStates = await GetFreshLinkStatesAsync(repoIdByName.Values.ToList());

            var tasks = repoItems.Select(async item =>
            {
                if (!repoIdByName.TryGetValue(item.RepoName, out var repoId))
                {
                    Interlocked.Increment(ref completedCount);
                    return (RepoId: 0, Success: false, ErrorMsg: (string?)"Repository not found");
                }

                linkStates.TryGetValue(repoId, out var linkState);
                var currentBranch = linkState?.Link.BranchName ?? item.BranchName;
                if (string.IsNullOrWhiteSpace(currentBranch))
                {
                    Interlocked.Increment(ref completedCount);
                    return (RepoId: repoId, Success: true, ErrorMsg: (string?)null);
                }

                await semaphore.WaitAsync(ct);
                try
                {
                    if (item.PrState == "open" && prNumberByRepoName.TryGetValue(item.RepoName, out var prNumber))
                    {
                        try
                        {
                            await ScopedExecutor.ExecuteAsync<WorkspacePullRequestService>(
                                svc => svc.ClosePullRequestAsync(WorkspaceId, repoId, prNumber, ct));
                        }
                        catch (Exception ex)
                        {
                            Logger.LogWarning(ex, "Failed to close PR {PrNumber} for repo {RepoName} before sync to default", prNumber, item.RepoName);
                        }
                    }

                    var (success, errMsg) = await ScopedExecutor.ExecuteAsync<WorkspaceGitService, (bool Success, string? ErrorMessage)>(
                        svc => svc.SyncToDefaultDirectAsync(
                            WorkspaceId, repoId, currentBranch,
                            deleteRemoteBranch && item.HasRemote, allowForceDeleteLocalBranch, ct));

                    return (RepoId: repoId, Success: success, ErrorMsg: errMsg);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Logger.LogError(ex, "Error syncing to default branch for repository {RepositoryId}", repoId);
                    return (RepoId: repoId, Success: false, ErrorMsg: (string?)"Sync to default branch failed. The GrayMoon Agent may be offline.");
                }
                finally
                {
                    semaphore.Release();
                    var c = Interlocked.Increment(ref completedCount);
                    if (total > 1)
                        job.ReportProgress($"Synchronized {c} of {total} to default branch");
                }
            });

            var results = await Task.WhenAll(tasks);

            // Recompute workspace-wide dependency/file-version stats exactly once, after every repo in this
            // "sync all to default" batch has finished, instead of racing N concurrent per-repo recomputes.
            await ScopedExecutor.ExecuteAsync<WorkspaceGitService>(
                svc => svc.RecomputeAndBroadcastWorkspaceSyncedAsync(WorkspaceId, ct));

            var successCount = results.Count(r => r.Success);
            var failureCount = results.Count(r => !r.Success);

            SafeInvoke(() =>
            {
                foreach (var (repoId, success, errMsg) in results)
                {
                    if (repoId == 0) continue;
                    if (success)
                    {
                        repositoryErrors.Remove(repoId);
                    }
                    else if (errMsg != null)
                    {
                        SetRepositoryError(repoId, errMsg);
                    }
                }

                if (total > 1 && failureCount == 0)
                    ToastService.Show($"Synced {successCount} of {total} repositories to default branch.");
            });
        }, new PageJobOptions
        {
            OnError = ex =>
            {
                Logger.LogError(ex, "Error syncing all repositories to default branch");
                SafeInvoke(() => errorMessage = "An error occurred while syncing to default branch. The GrayMoon Agent may be offline.");
            }
        });

    }
}
