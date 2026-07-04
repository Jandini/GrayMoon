using GrayMoon.App.Models;
using GrayMoon.App.Services;

namespace GrayMoon.App.Components.Pages;

public sealed partial class WorkspaceRepositories
{
    private VersionFilesCommitModalState _versionFilesCommitModal = new();

    private Task OnUpdateFilesClickAsync()
    {
        if (workspace == null || !HasRepositories || IsJobRunning)
            return Task.CompletedTask;

        errorMessage = null;

        StartPageJob("Updating file versions...", async (job, ct) =>
        {
            await ScopedExecutor.ExecuteAsync<WorkspaceFileVersionService>(async svc =>
            {
                var (updated, failed, error, _) = await svc.UpdateAllVersionsAsync(
                    WorkspaceId, selectedRepositoryIds: null, cancellationToken: ct);

                if (error == null)
                {
                    job.ReportProgress("Checking file versions...");
                    await svc.CheckAndPersistFileVersionStatusAsync(WorkspaceId, ct, forceFresh: true);
                }

                await InvokeAsync(async () =>
                {
                    if (_disposed) return;
                    if (error == null)
                        await RefreshFromSync();
                    StateHasChanged();
                    if (error != null)
                        errorMessage = error;
                    else if (failed > 0)
                        errorMessage = $"Updated {updated} line(s). {failed} file(s) could not be updated - check logs.";
                    else
                        ToastService.Show(updated > 0 ? "Versions updated in configured files." : "File versions are already up to date.");
                });
            });
        }, new PageJobOptions
        {
            RefreshOnSuccess = false,
            OnError = ex =>
            {
                Logger.LogError(ex, "Error updating file versions for WorkspaceId={WorkspaceId}", WorkspaceId);
                SafeInvoke(() => errorMessage = "Failed to update file versions. Please try again.");
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

        errorMessage = null;

        StartPageJob("Updating file versions...", async (job, ct) =>
        {
            await ScopedExecutor.ExecuteAsync<WorkspaceFileVersionService>(async svc =>
            {
                var repoIds = new HashSet<int> { repositoryId };
                var (updated, failed, error, _) = await svc.UpdateAllVersionsAsync(
                    WorkspaceId,
                    selectedRepositoryIds: repoIds,
                    filterPatternTokensToSelectedRepositories: false,
                    cancellationToken: ct);

                if (error != null)
                {
                    SafeInvoke(() => errorMessage = error);
                    return;
                }

                job.ReportProgress("Checking file versions...");
                await svc.CheckAndPersistFileVersionStatusAsync(WorkspaceId, ct, forceFresh: true);

                await InvokeAsync(async () =>
                {
                    if (_disposed) return;
                    await RefreshFromSync();
                    StateHasChanged();
                    if (failed > 0)
                        errorMessage = $"Updated {updated} line(s). {failed} file(s) could not be updated - check logs.";
                    else if (updated > 0)
                        ToastService.Show($"Updated {updated} line(s) in configured files.");
                    else
                        ToastService.Show("File versions are already up to date.");
                });
            });
        }, new PageJobOptions
        {
            RefreshOnSuccess = false,
            OnError = ex =>
            {
                Logger.LogError(ex, "Error updating file versions for repository {RepositoryId} in workspace {WorkspaceId}", repositoryId, WorkspaceId);
                SafeInvoke(() => errorMessage = "Failed to update file versions. Please try again.");
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

        errorMessage = null;
        CloseVersionFilesCommitModal();

        var jobLabel = shouldCommit ? "Updating and committing file versions..." : "Updating file versions...";
        StartPageJob(jobLabel, async (job, ct) =>
        {
            var repoIds = new HashSet<int> { repositoryId };

            // Two services needed (WorkspaceFileVersionService + WorkspaceGitService for optional commit) -
            // keep separate ScopedExecutor calls since each service is stateless.
            var (updated, failed, error, updatedFiles) = await ScopedExecutor.ExecuteAsync<
                WorkspaceFileVersionService,
                (int Updated, int Failed, string? Error, IReadOnlyList<(int RepositoryId, string RepoName, string FilePath)> UpdatedFiles)>(
                svc => svc.UpdateAllVersionsAsync(
                    WorkspaceId,
                    selectedRepositoryIds: repoIds,
                    filterPatternTokensToSelectedRepositories: false,
                    cancellationToken: ct));

            if (error != null)
            {
                SafeInvoke(() => errorMessage = error);
                return;
            }

            if (shouldCommit && updatedFiles is { Count: > 0 })
            {
                job.ReportProgress("Committing updated file versions...");
                var byRepo = updatedFiles
                    .GroupBy(x => (x.RepositoryId, x.RepoName))
                    .Select(g => (g.Key.RepositoryId, g.Key.RepoName, (IReadOnlyList<string>)g.Select(x => x.FilePath).Distinct().ToList()))
                    .ToList();
                var commitResults = await ScopedExecutor.ExecuteAsync<WorkspaceGitService, IReadOnlyList<(int RepoId, bool Committed, string? ErrorMessage)>>(
                    svc => svc.CommitFilePathsAsync(
                        WorkspaceId,
                        byRepo,
                        onProgress: (c, t, _) => job.ReportProgress($"Committed version files {c} of {t}"),
                        cancellationToken: ct));
                foreach (var (_, committed, errMsg) in commitResults)
                {
                    if (!string.IsNullOrEmpty(errMsg))
                    {
                        SafeInvoke(() => errorMessage = errMsg);
                        return;
                    }
                }
            }

            job.ReportProgress("Checking file versions...");
            await ScopedExecutor.ExecuteAsync<WorkspaceFileVersionService>(
                svc => svc.CheckAndPersistFileVersionStatusAsync(WorkspaceId, ct, forceFresh: true));

            await InvokeAsync(async () =>
            {
                if (_disposed) return;
                await RefreshFromSync();
                StateHasChanged();
                if (failed > 0)
                    errorMessage = $"Updated {updated} line(s). {failed} file(s) could not be updated - check logs.";
                else if (updated > 0)
                    ToastService.Show(shouldCommit
                        ? $"Updated and committed {updated} line(s) in configured files."
                        : $"Updated {updated} line(s) in configured files.");
                else
                    ToastService.Show("File versions are already up to date.");
            });
        }, new PageJobOptions
        {
            RefreshOnSuccess = false,
            OnError = ex =>
            {
                Logger.LogError(ex, "Error updating file versions for repository {RepositoryId} in workspace {WorkspaceId}", repositoryId, WorkspaceId);
                SafeInvoke(() => errorMessage = "Failed to update file versions. Please try again.");
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
