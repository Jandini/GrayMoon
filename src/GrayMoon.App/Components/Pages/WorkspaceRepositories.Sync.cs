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
        errorMessage = null;
        return RunSyncJobAsync(null, "Synchronizing...", skipDependencyLevelPersistence);
    }

    private Task SyncLevelAsync(List<int> repositoryIds)
    {
        if (workspace == null || repositoryIds == null || repositoryIds.Count == 0 || IsJobRunning) return Task.CompletedTask;
        errorMessage = null;
        var label = $"Synchronizing {repositoryIds.Count} {(repositoryIds.Count == 1 ? "repository" : "repositories")}...";
        return RunSyncJobAsync(repositoryIds, label, skipDependencyLevelPersistence: true);
    }

    private Task SyncSingleRepoAsync(int repositoryId)
    {
        if (workspace == null || !HasRepositories || IsJobRunning) return Task.CompletedTask;
        errorMessage = null;
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

    private Task RunSyncJobAsync(IReadOnlyList<int>? repositoryIds, string jobLabel, bool skipDependencyLevelPersistence)
    {
        JobService.StartJob(PageJobKey, jobLabel, async (job, ct) =>
        {
            try
            {
                var repoGitInfos = await ScopedExecutor.ExecuteAsync<WorkspaceSyncHandler, IReadOnlyDictionary<int, RepoGitVersionInfo>>(
                    svc => svc.RunSyncAsync(
                        WorkspaceId,
                        repositoryIds,
                        skipDependencyLevelPersistence,
                        cancellationToken: ct,
                        setProgress: job.ReportProgress,
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
                            repositoryErrors[repoId] = info.ErrorMessage;
                        else
                            repositoryErrors.Remove(repoId);
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
                SafeInvoke(() => errorMessage = $"Sync failed. {ex.Message}");
                throw;
            }
            catch (ConnectorHealthException ex)
            {
                Logger.LogError(ex, "Sync failed for workspace {WorkspaceId}", WorkspaceId);
                SafeInvoke(() => errorMessage = $"Sync failed. {ex.Message}");
                throw;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Sync failed for workspace {WorkspaceId}", WorkspaceId);
                SafeInvoke(() => errorMessage = "Sync failed. An unexpected error occurred. Check the logs for details.");
                throw;
            }
        });
        return Task.CompletedTask;
    }
}
