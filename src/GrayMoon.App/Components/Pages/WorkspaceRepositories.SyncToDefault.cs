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

        var nonDefaultRepoIds = repositoryIds
            .Select(id => TryGetLink(id))
            .Where(wr =>
            {
                if (wr == null || wr.IsOnTag || string.IsNullOrWhiteSpace(wr.BranchName))
                    return false;
                if (string.IsNullOrWhiteSpace(wr.DefaultBranchName))
                    return true;
                return !string.Equals(wr.BranchName, wr.DefaultBranchName, StringComparison.Ordinal);
            })
            .Select(wr => wr!.RepositoryId)
            .Distinct()
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

        // Synchronous pre-check using persisted state (no agent call needed)
        var checkResults = repositoryIds
            .Select(repoId =>
            {
                var wr = TryGetLink(repoId);
                return new SyncToDefaultCheckResult(repoId, wr?.DefaultBranchAheadCommits, wr?.BranchHasUpstream);
            })
            .ToList();
        var safeRepoIds = checkResults
            .Where(r => (r.DefaultAhead ?? 0) == 0 || IsPrMergedForRepo(r.RepoId))
            .Select(r => r.RepoId)
            .ToList();
        var blocked = checkResults
            .Where(r => (r.DefaultAhead ?? 0) > 0 && !IsPrMergedForRepo(r.RepoId))
            .ToList();

        foreach (var r in blocked)
        {
            var name = TryGetLink(r.RepoId)?.Repository?.RepositoryName ?? r.RepoId.ToString();
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

                    await InvokeAsync(async () =>
                    {
                        if (_disposed) return;
                        await RefreshFromSync();

                        // Rebuild check results with fresh HasUpstream from DB
                        _syncToDefaultCheckResults = _syncToDefaultCheckResults?
                            .Select(r =>
                            {
                                var wr2 = TryGetLink(r.RepoId);
                                return new SyncToDefaultCheckResult(r.RepoId, r.DefaultAhead, wr2?.BranchHasUpstream);
                            })
                            .ToList();

                        var repoItems = _syncToDefaultCheckResults?
                            .Select(r =>
                            {
                                var wr2 = TryGetLink(r.RepoId);
                                return new SyncToDefaultRepoItem(wr2?.Repository?.RepositoryName ?? r.RepoId.ToString(), wr2?.BranchName ?? "", r.HasUpstream == true, PrState: null, CommitsAhead: 0);
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
                    SafeInvoke(() => ToastService.Show("Failed to prepare sync to default."));
                    throw;
                }
            });
    }

    /// <summary>
    /// True if the workspace has a PR for this repository's current branch that is either merged
    /// or closed (treated the same as merged for sync-to-default safety checks).
    /// </summary>
    private bool IsPrMergedForRepo(int repositoryId)
    {
        if (!prByRepositoryId.TryGetValue(repositoryId, out var pr) || pr == null)
            return false;

        if (pr.IsMerged)
            return true;

        return string.Equals(pr.State, "closed", StringComparison.OrdinalIgnoreCase);
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
            var wr = await TryGetLinkAsync(repositoryId);
            var defaultAhead = wr?.DefaultBranchAheadCommits ?? 0;
            var hasUpstream = wr?.BranchHasUpstream == true;

            if (defaultAhead > 0)
            {
                try
                {
                    await WorkspacePageService.WorkspacePullRequestService.RefreshPullRequestsAsync(WorkspaceId, new[] { repositoryId }, force: true);
                    await ReloadWorkspaceDataFromFreshScopeAsync();
                    ApplySyncStateFromLoadedItems();
                    await InvokeAsync(StateHasChanged);
                }
                catch (Exception ex)
                {
                    Logger.LogDebug(ex, "PR refresh before sync-to-default check failed for RepositoryId={RepositoryId}", repositoryId);
                }
            }

            if (defaultAhead > 0 && !IsPrMergedForRepo(repositoryId))
            {
                ToastService.Show("Skipped sync to default: commits ahead of default branch and PR is not merged.");
                return;
            }

            if (hasUpstream)
            {
                var branchName = wr?.BranchName ?? currentBranchName;
                prByRepositoryId.TryGetValue(repositoryId, out var singlePr);
                var singlePrState = singlePr == null ? null : singlePr.IsMerged ? "merged" : singlePr.IsClosed ? "closed" : "open";
                ShowSyncToDefaultOptions(
                    "This will checkout the default branch, remove the current branch locally, and pull the latest.",
                    [new SyncToDefaultRepoItem(repositoryName!, branchName, true, singlePrState, defaultAhead)],
                    (deleteRemote, allowForce) => SyncToDefaultSingleRepoAfterCheckAsync(repositoryId, repositoryName, currentBranchName, deleteRemote, defaultBranch, allowForce));
            }
            else
            {
                await SyncToDefaultSingleRepoAfterCheckAsync(repositoryId, repositoryName, currentBranchName, deleteRemoteBranch: false, defaultBranch, allowForceDeleteLocalBranch: true);
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error preparing sync to default (repository {RepositoryId})", repositoryId);
            ToastService.Show("Failed to prepare sync to default.");
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

            if (success)
            {
                SafeInvoke(() => repositoryErrors.Remove(repositoryId));
                await InvokeAsync(async () => { if (_disposed) return; await RefreshFromSync(); });
            }
            else if (errMsg != null)
            {
                SafeInvoke(() => { repositoryErrors[repositoryId] = errMsg; });
            }
        }, new PageJobOptions
        {
            RefreshOnSuccess = false,
            OnError = ex =>
            {
                Logger.LogError(ex, "Error syncing to default branch for repository {RepositoryId}", repositoryId);
                SafeInvoke(() => { repositoryErrors[repositoryId] = "An error occurred while syncing to default branch. The GrayMoon Agent may be offline."; });
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

            var tasks = repositoryIds.Select(async repositoryId =>
            {
                var wr = await TryGetLinkAsync(repositoryId);
                var currentBranchName = wr?.BranchName;
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
                finally
                {
                    semaphore.Release();
                    var c = Interlocked.Increment(ref completedCount);
                    if (total > 1)
                        job.ReportProgress($"Synchronized {c} of {total} to default branch");
                }
            });

            var results = await Task.WhenAll(tasks);
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
                        repositoryErrors[repoId] = errMsg;
                        var repoName = TryGetLink(repoId)?.Repository?.RepositoryName ?? repoId.ToString();
                        ToastService.Show($"{repoName}: {errMsg}");
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

                    await InvokeAsync(async () =>
                    {
                        if (_disposed) return;
                        await RefreshFromSync();

                        var repoItems = eligibleIds
                            .Select(repoId =>
                            {
                                var wr2 = TryGetLink(repoId);
                                prByRepositoryId.TryGetValue(repoId, out var pr);
                                var prState = pr == null ? null : pr.IsMerged ? "merged" : pr.IsClosed ? "closed" : "open";
                                var commitsAhead = wr2?.DefaultBranchAheadCommits ?? 0;
                                return new SyncToDefaultRepoItem(
                                    wr2?.Repository?.RepositoryName ?? repoId.ToString(),
                                    wr2?.BranchName ?? "",
                                    wr2?.BranchHasUpstream == true,
                                    prState,
                                    commitsAhead);
                            })
                            .ToList();

                        ShowSyncToDefaultOptions(dialogMessage, repoItems, (deleteRemote, allowForce) => ExecuteSyncAllToDefaultAsync(repoItems, deleteRemote));
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
                    SafeInvoke(() => ToastService.Show("Failed to prepare sync to default."));
                    throw;
                }
            });
    }

    private async Task ExecuteSyncAllToDefaultAsync(
        IReadOnlyList<SyncToDefaultRepoItem> repoItems,
        bool deleteRemoteBranch)
    {
        if (workspace == null || repoItems.Count == 0 || IsJobRunning)
            return;

        var allLinks = await GetAllLinksForOperationAsync();
        var repoIdByName = allLinks.ToDictionary(
            wr => wr.Repository?.RepositoryName ?? string.Empty,
            wr => wr.RepositoryId);
        var prNumberByRepoName = new Dictionary<string, int>();
        foreach (var item in repoItems.Where(r => r.PrState == "open"))
        {
            if (!repoIdByName.TryGetValue(item.RepoName, out var repoId)) continue;
            if (prByRepositoryId.TryGetValue(repoId, out var pr) && pr != null && pr.Number > 0)
                prNumberByRepoName[item.RepoName] = pr.Number;
        }

        var total = repoItems.Count;
        errorMessage = null;

        StartPageJob("Synchronizing to default branch...", async (job, ct) =>
        {
            var maxParallel = Math.Max(1, WorkspaceOptions?.Value?.MaxParallelOperations ?? 16);
            var completedCount = 0;

            using var semaphore = new SemaphoreSlim(maxParallel, maxParallel);

            var tasks = repoItems.Select(async item =>
            {
                if (!repoIdByName.TryGetValue(item.RepoName, out var repoId))
                {
                    Interlocked.Increment(ref completedCount);
                    return (RepoId: 0, Success: false, ErrorMsg: (string?)"Repository not found");
                }

                var wr = TryGetLink(repoId);
                var currentBranch = wr?.BranchName ?? item.BranchName;
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
                            deleteRemoteBranch && item.HasRemote, allowForceDeleteLocalBranch: true, ct));

                    return (RepoId: repoId, Success: success, ErrorMsg: errMsg);
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
                        repositoryErrors[repoId] = errMsg;
                        var repoName = TryGetLink(repoId)?.Repository?.RepositoryName ?? repoId.ToString();
                        ToastService.Show($"{repoName}: {errMsg}");
                    }
                }

                if (total > 1)
                {
                    if (failureCount == 0)
                        ToastService.Show($"Synced {successCount} of {total} repositories to default branch.");
                    else
                        ToastService.Show($"Synced {successCount} of {total} repositories to default branch ({failureCount} failed).");
                }
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
