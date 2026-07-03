using GrayMoon.Abstractions.Exceptions;
using GrayMoon.App.Models;
using GrayMoon.App.Services;

namespace GrayMoon.App.Components.Pages;

public sealed partial class WorkspaceRepositories
{
    private UpdateModalState _updateModal = new();
    private UpdateModalState _updateAndPushModal = new();
    private LevelOnlyUpdateAndPushModalState _levelOnlyUpdateAndPushModal = new();

    /// <summary>Update button click: get update plan; if no updates, toast; else show modal (single vs multi-level).</summary>
    private async Task OnUpdateClickAsync()
    {
        if (workspace == null || workspaceRepositories.Count == 0 || IsJobRunning)
            return;

        if (workspaceRepositories.All(wr => wr.IsOnTag))
        {
            ToastService.Show("All repositories are on tags; checkout a branch first.");
            return;
        }

        var (updatePlan, _) = await ScopedExecutor.ExecuteAsync<WorkspaceGitService, (IReadOnlyList<SyncDependenciesRepoPayload> Payload, bool IsMultiLevel)>(
            svc => svc.GetUpdatePlanAsync(WorkspaceId));
        var repoIdsWithUpdates = updatePlan.Select(p => p.RepoId).ToHashSet();

        var reposOnDefault = workspaceRepositories
            .Where(wr => !wr.IsOnTag
                && !string.IsNullOrWhiteSpace(wr.DefaultBranchName)
                && string.Equals(wr.BranchName, wr.DefaultBranchName, StringComparison.Ordinal)
                && repoIdsWithUpdates.Contains(wr.RepositoryId))
            .ToList();

        if (reposOnDefault.Count > 0)
        {
            var repoItems = reposOnDefault
                .Select(wr => new DefaultBranchWarningItem(wr.Repository?.RepositoryName ?? $"repo {wr.RepositoryId}", wr.DefaultBranchName!))
                .ToList();
            ShowDefaultBranchWarning(
                "The following repositories are on their default branch. Update will commit dependency changes directly to the default (protected) branch.",
                repoItems,
                OpenUpdateModalAsync);
            return;
        }

        await OpenUpdateModalAsync();
    }

    private async Task OpenUpdateModalAsync()
    {
        try
        {
            _updateModal = _updateModal with
            {
                IsVisible = true
            };
            await InvokeAsync(StateHasChanged);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error getting update plan for workspace {WorkspaceId}", WorkspaceId);
            errorMessage = "Could not determine update plan. The GrayMoon Agent may be offline.";
        }
    }

    private void CloseUpdateModal()
    {
        _updateModal = _updateModal with { IsVisible = false };
        StateHasChanged();
    }

    private async Task OnUpdateProceedAsync((string? CommitMessage, bool IncludeDeps) args)
    {
        _updateModal = _updateModal with
        {
            IsVisible = false,
            LastCommitMessage = args.CommitMessage,
            LastIncludeDeps = args.IncludeDeps
        };
        await RunUpdateCoreAsync(args.CommitMessage, args.IncludeDeps);
    }

    /// <summary>Runs update (refresh, sync deps, commit per level, refresh version). Overlay shows progress.</summary>
    private Task RunUpdateCoreAsync(string? commitMessage = null, bool includeDepsInCommitMessage = true)
    {
        if (workspace == null || workspaceRepositories.Count == 0 || IsJobRunning)
            return Task.CompletedTask;

        errorMessage = null;

        StartPageJob("Updating dependencies...", async (job, ct) =>
        {
            await ScopedExecutor.ExecuteAsync<WorkspaceUpdateHandler>(svc =>
                svc.RunUpdateAsync(
                    WorkspaceId,
                    ct,
                    job.ReportProgress,
                    (repoId, msg) => SafeInvoke(() => { repositoryErrors[repoId] = msg; }),
                    repoIdsToUpdate: null,
                    commitMessage: commitMessage,
                    includeDepsInCommitMessage: includeDepsInCommitMessage));
        }, new PageJobOptions
        {
            RefreshOnCancel = true,
            OnError = ex =>
            {
                Logger.LogError(ex, "Error updating dependencies for workspace {WorkspaceId}", WorkspaceId);
                SafeInvoke(() => errorMessage = "Update failed. The GrayMoon Agent may be offline. Start the Agent and try again.");
            }
        });

        return Task.CompletedTask;
    }

    private async Task OnUpdateAndPushClickAsync()
    {
        if (workspace == null || workspaceRepositories.Count == 0 || IsJobRunning)
            return;

        if (workspaceRepositories.All(wr => wr.IsOnTag))
        {
            ToastService.Show("All repositories are on tags; checkout a branch first.");
            return;
        }

        var (updatePlan, _) = await ScopedExecutor.ExecuteAsync<WorkspaceGitService, (IReadOnlyList<SyncDependenciesRepoPayload> Payload, bool IsMultiLevel)>(
            svc => svc.GetUpdatePlanAsync(WorkspaceId));
        var repoIdsWithUpdates = updatePlan.Select(p => p.RepoId).ToHashSet();

        var reposOnDefault = workspaceRepositories
            .Where(wr => !wr.IsOnTag
                && !string.IsNullOrWhiteSpace(wr.DefaultBranchName)
                && string.Equals(wr.BranchName, wr.DefaultBranchName, StringComparison.Ordinal)
                && repoIdsWithUpdates.Contains(wr.RepositoryId))
            .ToList();

        if (reposOnDefault.Count > 0)
        {
            var repoItems = reposOnDefault
                .Select(wr => new DefaultBranchWarningItem(wr.Repository?.RepositoryName ?? $"repo {wr.RepositoryId}", wr.DefaultBranchName!))
                .ToList();
            ShowDefaultBranchWarning(
                "The following repositories are on their default branch. Update will commit dependency changes directly to the default (protected) branch.",
                repoItems,
                OpenUpdateAndPushModalAsync);
            return;
        }

        await OpenUpdateAndPushModalAsync();
    }

    private async Task OpenUpdateAndPushModalAsync()
    {
        _updateAndPushModal = _updateAndPushModal with { IsVisible = true };
        await InvokeAsync(StateHasChanged);
    }

    private void CloseUpdateAndPushModal()
    {
        _updateAndPushModal = _updateAndPushModal with { IsVisible = false };
        StateHasChanged();
    }

    private async Task OnUpdateAndPushProceedAsync((string? CommitMessage, bool IncludeDeps) args)
    {
        _updateAndPushModal = _updateAndPushModal with
        {
            IsVisible = false,
            LastCommitMessage = args.CommitMessage,
            LastIncludeDeps = args.IncludeDeps
        };
        await RunUpdateAndPushCoreAsync(args.CommitMessage, args.IncludeDeps);
    }

    private async Task OnLevelOnlyUpdateAndPushClickAsync()
    {
        if (workspace == null || workspaceRepositories.Count == 0 || IsJobRunning)
            return;

        var level = lowestLevelNeedingWork;
        if (!level.HasValue)
        {
            ToastService.Show("No repositories need work.");
            return;
        }

        if (workspaceRepositories.Where(wr => !wr.IsOnTag && (wr.DependencyLevel ?? 0) <= level).All(wr => wr.IsOnTag))
        {
            ToastService.Show("All repositories at this level are on tags; checkout a branch first.");
            return;
        }

        var (updatePlan, _) = await ScopedExecutor.ExecuteAsync<WorkspaceGitService, (IReadOnlyList<SyncDependenciesRepoPayload> Payload, bool IsMultiLevel)>(
            svc => svc.GetUpdatePlanAsync(WorkspaceId));
        var repoIdsWithUpdates = updatePlan.Select(p => p.RepoId).ToHashSet();

        var reposOnDefault = workspaceRepositories
            .Where(wr => !wr.IsOnTag
                && (wr.DependencyLevel ?? 0) <= level
                && !string.IsNullOrWhiteSpace(wr.DefaultBranchName)
                && string.Equals(wr.BranchName, wr.DefaultBranchName, StringComparison.Ordinal)
                && repoIdsWithUpdates.Contains(wr.RepositoryId))
            .ToList();

        if (reposOnDefault.Count > 0)
        {
            var repoItems = reposOnDefault
                .Select(wr => new DefaultBranchWarningItem(wr.Repository?.RepositoryName ?? $"repo {wr.RepositoryId}", wr.DefaultBranchName!))
                .ToList();
            ShowDefaultBranchWarning(
                $"The following repositories (up to Level {level}) are on their default branch. Update will commit dependency changes directly to the default (protected) branch.",
                repoItems,
                () => OpenLevelOnlyUpdateAndPushModalAsync(level.Value));
            return;
        }

        await OpenLevelOnlyUpdateAndPushModalAsync(level.Value);
    }

    private async Task OpenLevelOnlyUpdateAndPushModalAsync(int level)
    {
        _levelOnlyUpdateAndPushModal = _levelOnlyUpdateAndPushModal with { IsVisible = true, Level = level };
        await InvokeAsync(StateHasChanged);
    }

    private void CloseLevelOnlyUpdateAndPushModal()
    {
        _levelOnlyUpdateAndPushModal = _levelOnlyUpdateAndPushModal with { IsVisible = false };
        StateHasChanged();
    }

    private async Task OnLevelOnlyUpdateAndPushProceedAsync((string? CommitMessage, bool IncludeDeps) args)
    {
        var level = _levelOnlyUpdateAndPushModal.Level;
        _levelOnlyUpdateAndPushModal = _levelOnlyUpdateAndPushModal with
        {
            IsVisible = false,
            LastCommitMessage = args.CommitMessage,
            LastIncludeDeps = args.IncludeDeps
        };
        await RunLevelOnlyUpdateAndPushCoreAsync(level, args.CommitMessage, args.IncludeDeps);
    }

    private Task RunLevelOnlyUpdateAndPushCoreAsync(int level, string? commitMessage = null, bool includeDepsInCommitMessage = true)
    {
        if (workspace == null || workspaceRepositories.Count == 0 || IsJobRunning)
            return Task.CompletedTask;

        errorMessage = null;

        JobService.StartJob(PageJobKey, $"Updating Level {level}...", async (job, ct) =>
        {
            // Phase 1: update repos needing work up to the target level
            IReadOnlySet<int> syncedRepoIds = new HashSet<int>();
            try
            {
                var reposNeedingWork = workspaceRepositories
                    .Where(wr => !wr.IsOnTag && (wr.DependencyLevel ?? 0) <= level)
                    .Where(wr => (wr.UnmatchedDeps ?? 0) > 0 || (wr.OutOfDateFileRepos ?? 0) > 0)
                    .Select(wr => wr.RepositoryId)
                    .ToHashSet();

                syncedRepoIds = await ScopedExecutor.ExecuteAsync<WorkspaceUpdateHandler, IReadOnlySet<int>>(
                    svc => svc.RunUpdateAsync(
                        WorkspaceId,
                        ct,
                        job.ReportProgress,
                        (repoId, msg) => SafeInvoke(() => { repositoryErrors[repoId] = msg; }),
                        commitMessage: commitMessage,
                        includeDepsInCommitMessage: includeDepsInCommitMessage,
                        repoIdsToUpdate: reposNeedingWork,
                        maxLevel: level));

                await ReloadWorkspaceDataFromFreshScopeAsync();
                _ = InvokeAsync(() => { if (!_disposed) { ApplySyncStateFromWorkspace(); StateHasChanged(); } });
            }
            catch (OperationCanceledException)
            {
                await ReloadWorkspaceDataAfterCancelAsync();
                throw;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Level-Only Update & Push: update failed for workspace {WorkspaceId}", WorkspaceId);
                SafeInvoke(() => errorMessage = "Update failed. The GrayMoon Agent may be offline. Start the Agent and try again.");
                throw;
            }

            // Phase 2: determine push plan for repos that need pushing up to the target level
            job.ReportProgress("Preparing push...");
            IReadOnlySet<int> pushRepoIds;
            IReadOnlySet<string> requiredPackageIds;
            try
            {
                var plan = await BuildPushPlanAsync($"Up to Level {level} updated. Nothing to push.", ct, maxLevel: level);
                if (plan == null)
                    return;

                (pushRepoIds, requiredPackageIds) = plan.Value;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Level-Only Update & Push: failed to get push plan for workspace {WorkspaceId}", WorkspaceId);
                SafeInvoke(() => ShowOperationError("Push Failed", $"Level {level} updated but push plan could not be determined. The GrayMoon Agent may be offline."));
                throw;
            }

            // Phase 3: execute push (per-level restore of synced repos handled inside push service)
            try
            {
                await ExecutePushCoreAsync(job, ct, pushRepoIds, synchronizedPush: true, requiredPackageIds, syncedRepoIds);
            }
            catch (SynchronizedPushNotPossibleException ex)
            {
                SafeInvoke(() => ShowConfirm(
                    $"Synchronized push could not be completed because {ex.MissingPackagesCount} required package mappings are missing. Check NuGet connector configuration and token, then retry. Continue with normal push?",
                    () =>
                    {
                        JobService.StartJob(PageJobKey, "Preparing push...", async (j, c) =>
                        {
                            await ExecutePushCoreAsync(j, c, pushRepoIds, synchronizedPush: false, requiredPackageIds, syncedRepoIds);
                            await RestoreSyncedPackagesCoreAsync(syncedRepoIds, j, c);
                        });
                        return Task.CompletedTask;
                    },
                    confirmButtonText: "Continue"));
                return;
            }
        });

        return Task.CompletedTask;
    }

    private Task RunUpdateAndPushCoreAsync(string? commitMessage = null, bool includeDepsInCommitMessage = true)
    {
        if (workspace == null || workspaceRepositories.Count == 0 || IsJobRunning)
            return Task.CompletedTask;

        errorMessage = null;

        JobService.StartJob(PageJobKey, "Updating dependencies...", async (job, ct) =>
        {
            // Phase 1: update - fresh scope so DbContext does not compete with circuit page loads
            IReadOnlySet<int> syncedRepoIds = new HashSet<int>();
            try
            {
                var reposNeedingWork = workspaceRepositories
                    .Where(wr => !wr.IsOnTag)
                    .Where(wr => (wr.UnmatchedDeps ?? 0) > 0 || (wr.OutOfDateFileRepos ?? 0) > 0)
                    .Select(wr => wr.RepositoryId)
                    .ToHashSet();

                syncedRepoIds = await ScopedExecutor.ExecuteAsync<WorkspaceUpdateHandler, IReadOnlySet<int>>(
                    svc => svc.RunUpdateAsync(
                        WorkspaceId,
                        ct,
                        job.ReportProgress,
                        (repoId, msg) => SafeInvoke(() => { repositoryErrors[repoId] = msg; }),
                        repoIdsToUpdate: reposNeedingWork,
                        commitMessage: commitMessage,
                        includeDepsInCommitMessage: includeDepsInCommitMessage));

                // Unconditional reload so workspaceRepositories is current for Phase 2
                await ReloadWorkspaceDataFromFreshScopeAsync();
                _ = InvokeAsync(() => { if (!_disposed) { ApplySyncStateFromWorkspace(); StateHasChanged(); } });
            }
            catch (OperationCanceledException)
            {
                await ReloadWorkspaceDataAfterCancelAsync();
                throw;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Update & Push: update failed for workspace {WorkspaceId}", WorkspaceId);
                SafeInvoke(() => errorMessage = "Update failed. The GrayMoon Agent may be offline. Start the Agent and try again.");
                throw;
            }

            // Phase 2: determine push plan using fresh workspaceRepositories loaded above
            job.ReportProgress("Preparing push...");
            IReadOnlySet<int> pushRepoIds;
            IReadOnlySet<string> requiredPackageIds;
            try
            {
                var plan = await BuildPushPlanAsync("Update complete. Nothing to push.", ct);
                if (plan == null) return;
                (pushRepoIds, requiredPackageIds) = plan.Value;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Update & Push: failed to get push plan for workspace {WorkspaceId}", WorkspaceId);
                SafeInvoke(() => ShowOperationError("Push Failed", "Update succeeded but push plan could not be determined. The GrayMoon Agent may be offline."));
                throw;
            }

            // Phase 3: execute push (per-level restore of synced repos handled inside push service)
            try
            {
                await ExecutePushCoreAsync(job, ct, pushRepoIds, synchronizedPush: true, requiredPackageIds, syncedRepoIds);
            }
            catch (SynchronizedPushNotPossibleException ex)
            {
                SafeInvoke(() => ShowConfirm(
                    $"Synchronized push could not be completed because {ex.MissingPackagesCount} required package mappings are missing. Check NuGet connector configuration and token, then retry. Continue with normal push?",
                    () =>
                    {
                        JobService.StartJob(PageJobKey, "Preparing push...", async (j, c) =>
                        {
                            await ExecutePushCoreAsync(j, c, pushRepoIds, synchronizedPush: false, requiredPackageIds, syncedRepoIds);
                            await RestoreSyncedPackagesCoreAsync(syncedRepoIds, j, c);
                        });
                        return Task.CompletedTask;
                    },
                    confirmButtonText: "Continue"));
                return;
            }
        });

        return Task.CompletedTask;
    }

    private sealed record UpdateModalState
    {
        public bool IsVisible { get; init; }
        public string? LastCommitMessage { get; init; }
        public bool LastIncludeDeps { get; init; } = true;
    }

    private sealed record LevelOnlyUpdateAndPushModalState
    {
        public bool IsVisible { get; init; }
        public string? LastCommitMessage { get; init; }
        public bool LastIncludeDeps { get; init; } = true;
        public int Level { get; init; }
    }
}
