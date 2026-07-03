using GrayMoon.App.Services;

namespace GrayMoon.App.Components.Pages;

public sealed partial class WorkspaceRepositories
{
    /// <summary>Pull button click (when any repo has incoming commits): run commit sync (Pull) only for repos with incoming commits. Uses same overlay and merge/error handling as CommitSyncAsync/CommitSyncLevelAsync.</summary>
    private void OnPullClickAsync()
    {
        if (workspace == null || workspaceRepositories.Count == 0 || IsJobRunning)
            return;

        var repoIdsWithIncoming = workspaceRepositories
            .Where(wr =>
                !wr.IsOnTag
                && (wr.IncomingCommits ?? 0) > 0
                && !string.IsNullOrWhiteSpace(wr.BranchName)
                && !string.IsNullOrWhiteSpace(wr.DefaultBranchName)
                && string.Equals(wr.BranchName, wr.DefaultBranchName, StringComparison.Ordinal))
            .Select(wr => wr.RepositoryId)
            .ToList();
        if (repoIdsWithIncoming.Count == 0)
        {
            ToastService.Show("No repositories with incoming commits to pull.");
            return;
        }
        if (repoIdsWithIncoming.Count == 1)
            _ = CommitSyncAsync(repoIdsWithIncoming[0]);
        else
            _ = CommitSyncLevelAsync(repoIdsWithIncoming);
    }

    /// <summary>When user clicks the Commits badge and there are incoming commits: run Pull (commit sync) only, with same merge/error handling as CommitSyncAsync.</summary>
    private void OnPullBadgeClickAsync(int repositoryId)
    {
        if (workspace == null || IsJobRunning)
            return;
        if (IsRepoOnTag(repositoryId))
        {
            ToastService.Show(TagBlockedActionMessage);
            return;
        }
        _ = CommitSyncAsync(repositoryId);
    }

    private void ShowConfirmSyncCommits(int repositoryId)
    {
        ShowConfirm("Do you want to sync commits for this repository?", () => CommitSyncAsync(repositoryId));
    }

    private void ShowConfirmSyncCommitsLevel(List<int> repositoryIds)
    {
        var filtered = repositoryIds.Where(id => !IsRepoOnTag(id)).ToList();
        if (filtered.Count == 0)
        {
            ToastService.Show("All repositories in this level are on tags; checkout a branch first.");
            return;
        }
        if (filtered.Count <= 1)
            _ = CommitSyncLevelAsync(filtered);
        else
            ShowConfirm($"Do you want to sync commits for {filtered.Count} repositories?", () => CommitSyncLevelAsync(filtered));
    }

    private Task CommitSyncAsync(int repositoryId)
    {
        if (workspace == null || IsJobRunning)
            return Task.CompletedTask;
        if (IsRepoOnTag(repositoryId))
        {
            ToastService.Show(TagBlockedActionMessage);
            return Task.CompletedTask;
        }

        errorMessage = null;

        StartPageJob("Synchronizing commits...", async (job, ct) =>
        {
            await ScopedExecutor.ExecuteAsync<WorkspaceCommitSyncHandler>(svc =>
                svc.CommitSyncAsync(
                    WorkspaceId,
                    repositoryId,
                    ct,
                    msg => { job.ReportProgress(msg); return Task.CompletedTask; },
                    (id, err) => SafeInvoke(() =>
                    {
                        if (err is null)
                            repositoryErrors.Remove(id);
                        else
                            repositoryErrors[id] = err;
                    }),
                    msg => SafeInvoke(() => errorMessage = msg)));
        }, new PageJobOptions { RefreshOnCancel = true });

        return Task.CompletedTask;
    }

    private Task CommitSyncLevelAsync(List<int> repositoryIds)
    {
        if (workspace == null || IsJobRunning || repositoryIds == null || repositoryIds.Count == 0)
            return Task.CompletedTask;

        repositoryIds = repositoryIds.Where(id => !IsRepoOnTag(id)).ToList();
        if (repositoryIds.Count == 0)
        {
            ToastService.Show("All repositories are on tags; checkout a branch first.");
            return Task.CompletedTask;
        }

        errorMessage = null;

        StartPageJob("Synchronizing commits...", async (job, ct) =>
        {
            await ScopedExecutor.ExecuteAsync<WorkspaceCommitSyncHandler>(svc =>
                svc.CommitSyncLevelAsync(
                    WorkspaceId,
                    repositoryIds,
                    ct,
                    (completed, total) => { job.ReportProgress($"Synchronized commits {completed} of {total}"); return Task.CompletedTask; },
                    (id, err) => SafeInvoke(() =>
                    {
                        if (err is null)
                            repositoryErrors.Remove(id);
                        else
                            repositoryErrors[id] = err;
                    }),
                    msg => SafeInvoke(() => errorMessage = msg)));
        }, new PageJobOptions { RefreshOnCancel = true });

        return Task.CompletedTask;
    }
}
