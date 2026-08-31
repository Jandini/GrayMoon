using GrayMoon.App.Models;

namespace GrayMoon.App.Components.Pages;

public sealed partial class WorkspaceRepositories
{
    private VersionFilesCommitModalState _versionFilesCommitModal = new();

    private Task OnUpdateFilesClickAsync()
    {
        if (workspace == null || !HasRepositories || IsJobRunning)
            return Task.CompletedTask;

        StartPageJob("Updating file versions...", async (job, ct) =>
        {
            var result = await ScopedExecutor.ExecuteAsync<IWorkspaceFileOperations, WorkspaceFileVersionUpdateResult>(
                svc => svc.UpdateVersionsAsync(
                    WorkspaceId,
                    ct,
                    checkAfter: true,
                    progress: job.ToOperationProgress()));

            await InvokeAsync(async () =>
            {
                if (_disposed) return;
                if (result.Error == null)
                    await RefreshFromSync();
                StateHasChanged();
                if (result.Error != null)
                    SetPageError(result.Error);
                else if (result.Failed > 0)
                    SetPageError($"Updated {result.Updated} line(s). {result.Failed} file(s) could not be updated - check logs.");
                else
                    ToastService.Show(result.Updated > 0 ? "Versions updated in configured files." : "File versions are already up to date.");
            });
        }, new PageJobOptions
        {
            RefreshOnSuccess = false,
            OnError = ex =>
            {
                Logger.LogError(ex, "Error updating file versions for WorkspaceId={WorkspaceId}", WorkspaceId);
                SafeInvoke(() => SetPageError("Failed to update file versions. Please try again."));
            }
        });

        return Task.CompletedTask;
    }

    private Task UpdateSingleRepositoryFileVersionsAsync(int repositoryId)
    {
        if (workspace == null || IsJobRunning)
            return Task.CompletedTask;
        if (IsRepoOnTag(repositoryId))
        {
            ToastService.Show(TagBlockedActionMessage);
            return Task.CompletedTask;
        }

        StartPageJob("Updating file versions...", async (job, ct) =>
        {
            var repoIds = new HashSet<int> { repositoryId };
            var result = await ScopedExecutor.ExecuteAsync<IWorkspaceFileOperations, WorkspaceFileVersionUpdateResult>(
                svc => svc.UpdateVersionsAsync(
                    WorkspaceId,
                    ct,
                    selectedRepositoryIds: repoIds,
                    filterPatternTokensToSelectedRepositories: false,
                    checkAfter: true,
                    progress: job.ToOperationProgress()));

            if (result.Error != null)
            {
                SafeInvoke(() => SetPageError(result.Error));
                return;
            }

            await InvokeAsync(async () =>
            {
                if (_disposed) return;
                await RefreshFromSync();
                StateHasChanged();
                if (result.Failed > 0)
                    SetPageError($"Updated {result.Updated} line(s). {result.Failed} file(s) could not be updated - check logs.");
                else if (result.Updated > 0)
                    ToastService.Show($"Updated {result.Updated} line(s) in configured files.");
                else
                    ToastService.Show("File versions are already up to date.");
            });
        }, new PageJobOptions
        {
            RefreshOnSuccess = false,
            OnError = ex =>
            {
                Logger.LogError(ex, "Error updating file versions for repository {RepositoryId} in workspace {WorkspaceId}", repositoryId, WorkspaceId);
                SafeInvoke(() => SetPageError("Failed to update file versions. Please try again."));
            }
        });

        return Task.CompletedTask;
    }

    private void OnFileDependencyBadgeClick(int repositoryId)
    {
        clickedDependencyBadges.Add(repositoryId);
        _ = ShowFileVersionsCommitFlowAsync(repositoryId);
        StateHasChanged();
    }

    private async Task ShowFileVersionsCommitFlowAsync(int repositoryId)
    {
        if (workspace == null || IsJobRunning)
            return;
        if (IsRepoOnTag(repositoryId))
        {
            ToastService.Show(TagBlockedActionMessage);
            return;
        }

        var repo = TryGetLink(repositoryId);
        var repoName = repo?.Repository?.RepositoryName;

        if (repo != null
            && !string.IsNullOrWhiteSpace(repo.DefaultBranchName)
            && string.Equals(repo.BranchName, repo.DefaultBranchName, StringComparison.Ordinal))
        {
            ShowDefaultBranchWarning(
                "The following repository is on its default branch. Updating file versions will commit changes directly to the default (protected) branch.",
                new[] { new DefaultBranchWarningItem(repoName ?? $"repo {repositoryId}", repo.DefaultBranchName!) },
                () => ShowVersionFilesCommitModalAsync(repositoryId, repoName));
            return;
        }

        await ShowVersionFilesCommitModalAsync(repositoryId, repoName);
    }

    private Task ShowVersionFilesCommitModalAsync(int repositoryId, string? repoName)
    {
        var lines = GetMismatchedFileVersionLines(repositoryId);
        var distinctFiles = lines
            .Select(l => l.FileName)
            .Where(f => !string.IsNullOrEmpty(f))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        _versionFilesCommitModal = _versionFilesCommitModal with
        {
            IsVisible = true,
            RepoName = repoName,
            Files = distinctFiles,
            IsBusy = false,
            PendingAction = shouldCommit => CommitFileVersionUpdateAsync(repositoryId, shouldCommit),
        };
        StateHasChanged();
        return Task.CompletedTask;
    }

    private async Task OnVersionFilesCommitProceedAsync(bool shouldCommit)
    {
        var action = _versionFilesCommitModal.PendingAction;
        if (action == null)
            return;
        _versionFilesCommitModal = _versionFilesCommitModal with { IsBusy = true };
        StateHasChanged();
        await action(shouldCommit);
    }

    private void CloseVersionFilesCommitModal()
    {
        _versionFilesCommitModal = _versionFilesCommitModal with
        {
            IsVisible = false,
            IsBusy = false,
            PendingAction = null,
        };
        StateHasChanged();
    }

    private Task CommitFileVersionUpdateAsync(int repositoryId, bool shouldCommit)
    {
        if (workspace == null || IsJobRunning)
        {
            CloseVersionFilesCommitModal();
            return Task.CompletedTask;
        }

        CloseVersionFilesCommitModal();

        var jobLabel = shouldCommit ? "Updating and committing file versions..." : "Updating file versions...";
        StartPageJob(jobLabel, async (job, ct) =>
        {
            var repoIds = new HashSet<int> { repositoryId };
            var result = await ScopedExecutor.ExecuteAsync<IWorkspaceFileOperations, WorkspaceFileVersionUpdateResult>(
                svc => svc.UpdateVersionsAsync(
                    WorkspaceId,
                    ct,
                    selectedRepositoryIds: repoIds,
                    filterPatternTokensToSelectedRepositories: false,
                    commitUpdatedFiles: shouldCommit,
                    checkAfter: true,
                    progress: job.ToOperationProgress()));

            if (result.Error != null)
            {
                SafeInvoke(() => SetPageError(result.Error));
                return;
            }

            await InvokeAsync(async () =>
            {
                if (_disposed) return;
                await RefreshFromSync();
                StateHasChanged();
                if (result.Failed > 0)
                    SetPageError($"Updated {result.Updated} line(s). {result.Failed} file(s) could not be updated - check logs.");
                else if (result.Updated > 0)
                    ToastService.Show(shouldCommit
                        ? $"Updated and committed {result.Updated} line(s) in configured files."
                        : $"Updated {result.Updated} line(s) in configured files.");
                else
                    ToastService.Show("File versions are already up to date.");
            });
        }, new PageJobOptions
        {
            RefreshOnSuccess = false,
            OnError = ex =>
            {
                Logger.LogError(ex, "Error updating file versions for repository {RepositoryId} in workspace {WorkspaceId}", repositoryId, WorkspaceId);
                SafeInvoke(() => SetPageError("Failed to update file versions. Please try again."));
            }
        });

        return Task.CompletedTask;
    }

    private sealed record VersionFilesCommitModalState
    {
        public bool IsVisible { get; init; }
        public string? RepoName { get; init; }
        public IReadOnlyList<string> Files { get; init; } = Array.Empty<string>();
        public bool IsBusy { get; init; }
        public Func<bool, Task>? PendingAction { get; init; }
    }
}
