using GrayMoon.Abstractions.Exceptions;
using GrayMoon.App.Models;
using GrayMoon.App.Services;

namespace GrayMoon.App.Components.Pages;

public sealed partial class WorkspaceRepositories
{
    private Task SyncAsync()
    {
        if (workspace == null || !HasRepositories || IsJobRunning) return Task.CompletedTask;
        var skipDependencyLevelPersistence = !string.IsNullOrEmpty(errorMessage);
        return RunSyncJobAsync(null, "Synchronizing...", skipDependencyLevelPersistence);
    }

    private Task SyncLevelAsync(List<int> repositoryIds)
    {
        if (workspace == null || repositoryIds == null || repositoryIds.Count == 0 || IsJobRunning) return Task.CompletedTask;
        var label = $"Synchronizing {repositoryIds.Count} {(repositoryIds.Count == 1 ? "repository" : "repositories")}...";
        return RunSyncJobAsync(repositoryIds, label, skipDependencyLevelPersistence: true);
    }

    private Task SyncSingleRepoAsync(int repositoryId)
    {
        if (workspace == null || !HasRepositories || IsJobRunning) return Task.CompletedTask;
        return RunSyncJobAsync(new[] { repositoryId }, "Synchronizing repository...", skipDependencyLevelPersistence: true);
    }

    private void ShowConfirmSyncLevel(List<int> repositoryIds)
    {
        var filtered = repositoryIds.Where(id => !IsRepoOnTag(id)).ToList();
        if (filtered.Count == 0)
        {
            ToastService.Show("All repositories in this level are on tags; checkout a branch first.");
            return;
        }
        const int confirmThreshold = 10;
        if (filtered.Count < confirmThreshold)
            _ = SyncLevelAsync(filtered);
        else
            ShowConfirm($"Do you want to sync {filtered.Count} repositories in this level?", () => SyncLevelAsync(filtered));
    }

    private async Task FetchLevelAsync(int? levelKey)
    {
        if (workspace == null || IsJobRunning) return;
        var repoIds = (await GetRepositoryIdsAtLevelAsync(levelKey)).ToList();
        if (repoIds.Count == 0) return;
        var label = $"Fetching {repoIds.Count} {(repoIds.Count == 1 ? "repository" : "repositories")}...";
        StartPageJob(label, async (job, ct) =>
        {
            var result = await ScopedExecutor.ExecuteAsync<IWorkspaceSyncOperations, OperationResult>(
                svc => svc.QuickFetchAsync(
                    WorkspaceId,
                    repoIds,
                    job.ToOperationProgress(),
                    ct),
                ct);
            SafeInvoke(() => ApplyFetchResult(result, repoIds));
        }, new PageJobOptions
        {
            CancelToast = "Fetch cancelled.",
            OnError = ex =>
            {
                Logger.LogError(ex, "Fetch failed for a dependency level in workspace {WorkspaceId}", WorkspaceId);
                SafeInvoke(() => SetPageError("Fetch failed. Check the logs for details."));
            }
        });
    }

    private Task QuickFetchAsync()
    {
        if (workspace == null || !HasRepositories || IsJobRunning) return Task.CompletedTask;
        JobService.StartJob(PageJobKey, "Fetching commits...", async (job, ct) =>
        {
            try
            {
                var result = await ScopedExecutor.ExecuteAsync<IWorkspaceSyncOperations, OperationResult>(
                    svc => svc.QuickFetchAsync(
                        WorkspaceId,
                        repositoryIds: null,
                        job.ToOperationProgress(),
                        ct),
                    ct);

                await InvokeAsync(async () =>
                {
                    if (_disposed) return;
                    await ReloadWorkspaceDataFromFreshScopeAsync();
                    ApplySyncStateFromLoadedItems();
                    ApplyFetchResult(result, _linkByRepoId.Keys);
                    StateHasChanged();
                });
            }
            catch (OperationCanceledException)
            {
                await ReloadWorkspaceDataAfterCancelAsync();
                throw;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Quick Fetch failed for workspace {WorkspaceId}", WorkspaceId);
                SafeInvoke(() => SetPageError("Fetch failed. Check the logs for details."));
                throw;
            }
        });
        return Task.CompletedTask;
    }

    private void ApplyFetchResult(OperationResult result, IEnumerable<int>? attemptedRepoIds)
    {
        if (result.RepoErrors is { Count: > 0 })
            ApplyRepositoryErrors(result.RepoErrors);

        if (attemptedRepoIds != null)
        {
            foreach (var id in attemptedRepoIds)
            {
                if (result.RepoErrors is null || !result.RepoErrors.ContainsKey(id))
                    ClearRepositoryError(id);
            }
        }

        if (result.RepoErrors is not { Count: > 0 }
            && !result.Success
            && !string.IsNullOrWhiteSpace(result.Error))
        {
            SetPageError(result.Error);
        }
    }

    private Task RunSyncJobAsync(IReadOnlyList<int>? repositoryIds, string jobLabel, bool skipDependencyLevelPersistence)
    {
        JobService.StartJob(PageJobKey, jobLabel, async (job, ct) =>
        {
            try
            {
                var repoGitInfos = await ScopedExecutor.ExecuteAsync<IWorkspaceSyncOperations, IReadOnlyDictionary<int, RepoGitVersionInfo>>(
                    svc => svc.SyncAsync(
                        WorkspaceId,
                        repositoryIds,
                        skipDependencyLevelPersistence,
                        cancellationToken: ct,
                        progress: job.ToOperationProgress(),
                        updateRepoGitInfo: (repoId, info) => SafeInvoke(() =>
                        {
                            if (_linkByRepoId.TryGetValue(repoId, out var wr))
                            {
                                wr.GitVersion = info.Version == "-" ? null : info.Version;
                                wr.BranchName = info.Branch == "-" ? null : info.Branch;
                                wr.Projects = info.Projects;
                                wr.OutgoingCommits = info.OutgoingCommits;
                                wr.IncomingCommits = info.IncomingCommits;
                            }
                        }),
                        setRepoSyncStatus: (repoId, status) => repoSyncStatus[repoId] = status));

                await InvokeAsync(async () =>
                {
                    if (_disposed) return;
                    await ReloadWorkspaceDataFromFreshScopeAsync();
                    ApplySyncStateFromLoadedItems();
                    foreach (var (repoId, info) in repoGitInfos)
                    {
                        if (!string.IsNullOrWhiteSpace(info.ErrorMessage))
                            SetRepositoryError(repoId, info.ErrorMessage);
                        else
                            ClearRepositoryError(repoId);
                    }
                    StateHasChanged();
                });
            }
            catch (OperationCanceledException)
            {
                await ReloadWorkspaceDataAfterCancelAsync();
                throw;
            }
            catch (AgentNotConnectedException ex)
            {
                Logger.LogError(ex, "Sync failed for workspace {WorkspaceId}", WorkspaceId);
                SafeInvoke(() => SetPageError($"Sync failed. {ex.Message}"));
                throw;
            }
            catch (ConnectorHealthException ex)
            {
                Logger.LogError(ex, "Sync failed for workspace {WorkspaceId}", WorkspaceId);
                SafeInvoke(() => SetPageError($"Sync failed. {ex.Message}"));
                throw;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Sync failed for workspace {WorkspaceId}", WorkspaceId);
                SafeInvoke(() => SetPageError("Sync failed. An unexpected error occurred. Check the logs for details."));
                throw;
            }
        });
        return Task.CompletedTask;
    }
}
