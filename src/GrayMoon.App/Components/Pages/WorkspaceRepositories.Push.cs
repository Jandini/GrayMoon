using GrayMoon.Abstractions.Exceptions;
using GrayMoon.App.Models;
using GrayMoon.App.Services;

namespace GrayMoon.App.Components.Pages;

public sealed partial class WorkspaceRepositories
{
    private PushWithDependenciesModalState _pushWithDependenciesModal = new();

    /// <summary>Push button click: get push plan; filter to repos with unpushed commits or no upstream branch; show modal only if at least one dependency repo needs push; otherwise push immediately.</summary>
    private async Task OnPushClickAsync()
    {
        if (workspace == null || !HasRepositories || IsJobRunning)
            return;

        try
        {
            var allLinks = await GetAllLinksForOperationAsync();
            var (_, pushRepoIds, hasUnpushed) = await WorkspacePushHandler.GetPushPlanAsync(
                WorkspaceId,
                allLinks,
                CancellationToken.None);
            if (!hasUnpushed || pushRepoIds.Count == 0)
            {
                ToastService.Show("No repositories to push.");
                return;
            }

            var repoIdsWithUnpushed = pushRepoIds;
            var repoIdsThatNeedPush = allLinks
                .Where(wr => !wr.IsOnTag && (wr.OutgoingCommits ?? 0) > 0)
                .Select(wr => wr.RepositoryId)
                .ToHashSet();
            var depInfo = await WorkspaceDependencyService.GetPushDependencyInfoForRepoSetAsync(
                WorkspaceId,
                repoIdsWithUnpushed,
                CancellationToken.None);
            if (depInfo == null)
            {
                ToastService.ShowError("Could not load push plan. Try again.");
                return;
            }
            _pushWithDependenciesModal = _pushWithDependenciesModal with
            {
                Info = depInfo,
                RepoIdsToPush = repoIdsWithUnpushed,
                RepoId = 0,
                RepoName = null
            };

            // Push immediately without dialog when there are no deps, or when no dependency repo needs push.
            if ((depInfo.PayloadForRepo.RequiredPackages.Count == 0 && depInfo.DependencyRepoIds.Count == 0)
                || !WorkspaceDependencyService.ShouldShowSynchronizedPushModal(depInfo, repoIdsThatNeedPush))
            {
                await OnPushWithDependenciesProceedAsync(synchronizedPush: false);
                return;
            }

            _pushWithDependenciesModal = _pushWithDependenciesModal with { IsVisible = true };
            await InvokeAsync(StateHasChanged);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error getting push plan for workspace {WorkspaceId}", WorkspaceId);
            errorMessage = "Could not determine push plan. The GrayMoon Agent may be offline.";
        }
    }

    /// <summary>When user clicks the not-upstreamed badge: check dependencies. Show modal only if at least one dependency repo needs push; otherwise push directly.</summary>
    private async Task OnPushBadgeClickAsync(int repositoryId, string? branchName)
    {
        if (workspace == null || IsJobRunning)
            return;
        if (IsRepoOnTag(repositoryId))
        {
            ToastService.Show(TagBlockedActionMessage);
            return;
        }

        var repo = TryGetLink(repositoryId);
        if (repo != null
            && !string.IsNullOrWhiteSpace(repo.DefaultBranchName)
            && string.Equals(repo.BranchName, repo.DefaultBranchName, StringComparison.Ordinal))
        {
            var repoName = repo.Repository?.RepositoryName ?? $"repo {repositoryId}";
            ShowDefaultBranchWarning(
                "The following repository is on its default branch. Pushing will commit directly to the default (protected) branch.",
                new[] { new DefaultBranchWarningItem(repoName, repo.DefaultBranchName!) },
                () => PushBadgeClickCoreAsync(repositoryId, branchName));
            return;
        }

        await PushBadgeClickCoreAsync(repositoryId, branchName);
    }

    private async Task PushBadgeClickCoreAsync(int repositoryId, string? branchName)
    {
        try
        {
            var allLinks = await GetAllLinksForOperationAsync();
            var repoIdsThatNeedPush = allLinks
                .Where(wr => !wr.IsOnTag && (wr.OutgoingCommits ?? 0) > 0)
                .Select(wr => wr.RepositoryId)
                .ToHashSet();
            var depInfo = await WorkspaceDependencyService.GetPushDependencyInfoForRepoAsync(
                WorkspaceId,
                repositoryId,
                CancellationToken.None);

            if (depInfo == null || !WorkspaceDependencyService.ShouldShowSynchronizedPushModal(depInfo, repoIdsThatNeedPush))
            {
                await PushSingleRepositoryWithUpstreamAsync(repositoryId, branchName);
                return;
            }

            var repoName = TryGetLink(repositoryId)?.Repository?.RepositoryName;
            _pushWithDependenciesModal = _pushWithDependenciesModal with
            {
                IsVisible = true,
                Info = depInfo,
                RepoIdsToPush = null,
                RepoId = repositoryId,
                BranchName = branchName,
                RepoName = repoName
            };
            await InvokeAsync(StateHasChanged);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error getting push dependency info for repository {RepositoryId}", repositoryId);
            ToastService.ShowError("Could not load dependency info. Try again.");
        }
    }

    private void ClosePushWithDependenciesModal()
    {
        _pushWithDependenciesModal = _pushWithDependenciesModal with { IsVisible = false, Info = null, RepoIdsToPush = null };
    }

    /// <summary>Proceed from PushWithDependencies modal: synchronized push = sync registries then level-order push with wait; otherwise push all repos in parallel (MaxParallelOperations).</summary>
    private Task OnPushWithDependenciesProceedAsync(bool synchronizedPush)
    {
        if (_pushWithDependenciesModal.Info == null || workspace == null)
            return Task.CompletedTask;

        var repoIds = _pushWithDependenciesModal.RepoIdsToPush != null
            ? _pushWithDependenciesModal.RepoIdsToPush
            : _pushWithDependenciesModal.Info.DependencyRepoIds.Concat(new[] { _pushWithDependenciesModal.RepoId }).ToHashSet();
        var requiredPackageIds = _pushWithDependenciesModal.Info.PayloadForRepo.RequiredPackages
            .Select(r => r.PackageId?.Trim())
            .Where(id => !string.IsNullOrEmpty(id))
            .Cast<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        ClosePushWithDependenciesModal();

        JobService.StartJob(PageJobKey, "Preparing push...", async (job, ct) =>
        {
            try
            {
                await ExecutePushCoreAsync(job, ct, repoIds, synchronizedPush, requiredPackageIds);
            }
            catch (SynchronizedPushNotPossibleException ex)
            {
                // Job completes normally; user confirms to start a new push job without synchronized mode.
                SafeInvoke(() => ShowConfirm(
                    $"Synchronized push could not be completed because {ex.MissingPackagesCount} required package mappings are missing. Check NuGet connector configuration and token, then retry. Continue with normal push?",
                    () =>
                    {
                        JobService.StartJob(PageJobKey, "Preparing push...", (j, c) =>
                            ExecutePushCoreAsync(j, c, repoIds, synchronizedPush: false, requiredPackageIds));
                        return Task.CompletedTask;
                    },
                    confirmButtonText: "Continue"));
            }
        });

        return Task.CompletedTask;
    }

    private async Task<(IReadOnlySet<int> PushRepoIds, IReadOnlySet<string> RequiredPackageIds)?> BuildPushPlanAsync(
        string emptyMessage, CancellationToken ct, int? maxLevel = null)
    {
        await using var planScope = ServiceScopeFactory.CreateAsyncScope();
        var planPushHandler = planScope.ServiceProvider.GetRequiredService<WorkspacePushHandler>();
        var planDepService = planScope.ServiceProvider.GetRequiredService<WorkspaceDependencyService>();
        var allLinks = await GetAllLinksForOperationAsync();
        var (_, pushRepoIds, hasUnpushed) = await planPushHandler.GetPushPlanAsync(WorkspaceId, allLinks, ct, maxLevel);
        if (!hasUnpushed || pushRepoIds.Count == 0)
        {
            SafeInvoke(() => ToastService.Show(emptyMessage));
            return null;
        }
        var depInfo = await planDepService.GetPushDependencyInfoForRepoSetAsync(WorkspaceId, pushRepoIds, ct);
        IReadOnlySet<string> requiredPackageIds = depInfo?.PayloadForRepo?.RequiredPackages
            .Select(r => r.PackageId?.Trim())
            .Where(id => !string.IsNullOrEmpty(id))
            .Cast<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase) ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return (pushRepoIds, requiredPackageIds);
    }

    private async Task ExecutePushCoreAsync(
        BackgroundJobHandle job,
        CancellationToken ct,
        IReadOnlySet<int> repoIds,
        bool synchronizedPush,
        IReadOnlySet<string> requiredPackageIds,
        IReadOnlySet<int>? syncedRepoIds = null)
    {
        try
        {
            await ScopedExecutor.ExecuteAsync<WorkspacePushHandler>(svc =>
                svc.RunPushWithDependenciesAsync(
                    WorkspaceId,
                    repoIds,
                    synchronizedPush,
                    requiredPackageIds,
                    job.ReportProgress,
                    ToastService.ShowError,
                    syncedRepoIds: syncedRepoIds,
                    cancellationToken: ct));
        }
        catch (OperationCanceledException)
        {
            SafeInvoke(() => ToastService.Show("Push cancelled."));
            throw;
        }
        finally
        {
            await InvokeAsync(async () =>
            {
                if (_disposed) return;
                await RefreshFromSync();
            });
        }
    }

    /// <summary>Push with upstream for a single repository (e.g. when user clicks the not-upstreamed badge). Uses the page overlay.</summary>
    private Task PushSingleRepositoryWithUpstreamAsync(int repositoryId, string? branchName)
    {
        if (workspace == null || IsJobRunning)
            return Task.CompletedTask;

        StartPageJob("Setting upstream...", async (job, ct) =>
        {
            var (success, pushError) = await ScopedExecutor.ExecuteAsync<WorkspacePushHandler, (bool, string?)>(
                svc => svc.PushSingleRepositoryWithUpstreamAsync(WorkspaceId, repositoryId, branchName, job.ReportProgress, ct));

            if (success)
                await InvokeAsync(async () => { if (_disposed) return; await RefreshFromSync(); });
            else
                SafeInvoke(() => ToastService.ShowError(pushError ?? "Push failed."));
        }, new PageJobOptions
        {
            RefreshOnSuccess = false,
            CancelToast = "Push cancelled.",
            OnError = ex =>
            {
                Logger.LogError(ex, "Push with upstream failed for repository {RepositoryId}", repositoryId);
                SafeInvoke(() => ToastService.ShowError(ex.Message));
            }
        });

        return Task.CompletedTask;
    }

    private Task RestorePackagesAsync()
    {
        if (workspace == null || IsJobRunning)
            return Task.CompletedTask;

        StartPageJob("Restoring packages...", RestorePackagesCoreAsync, new PageJobOptions { RefreshOnSuccess = false });

        return Task.CompletedTask;
    }

    private async Task RestorePackagesCoreAsync(BackgroundJobHandle job, CancellationToken ct)
    {
        job.ReportProgress("Restoring packages...");
        try
        {
            var count = await ScopedExecutor.ExecuteAsync<WorkspaceGitService, int>(
                svc => svc.RestoreAllWorkspacePackagesAsync(WorkspaceId, job.ReportProgress, ct));

            if (count > 0)
                SafeInvoke(() => ToastService.Show($"Restored packages in {count} {(count == 1 ? "project" : "projects")}"));
        }
        catch (OperationCanceledException)
        {
            SafeInvoke(() => ToastService.Show("Restore cancelled."));
            throw;
        }
        catch (AgentNotConnectedException ex)
        {
            Logger.LogError(ex, "Restore packages failed (agent not connected) for workspace {WorkspaceId}", WorkspaceId);
            SafeInvoke(() => ToastService.ShowError($"Restore failed. {ex.Message}"));
            throw;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Restore packages failed for workspace {WorkspaceId}", WorkspaceId);
            SafeInvoke(() => ToastService.ShowError($"Restore failed: {ex.Message}"));
            throw;
        }
    }

    private async Task RestoreSyncedPackagesCoreAsync(IReadOnlySet<int> syncedRepoIds, BackgroundJobHandle job, CancellationToken ct)
    {
        if (syncedRepoIds.Count == 0) return;
        job.ReportProgress("Restoring packages...");
        try
        {
            var count = await ScopedExecutor.ExecuteAsync<WorkspaceGitService, int>(
                svc => svc.RestoreSyncedWorkspacePackagesAsync(WorkspaceId, syncedRepoIds, job.ReportProgress, ct));

            if (count > 0)
                SafeInvoke(() => ToastService.Show($"Restored packages in {count} {(count == 1 ? "project" : "projects")}"));
        }
        catch (OperationCanceledException)
        {
            SafeInvoke(() => ToastService.Show("Restore cancelled."));
            throw;
        }
        catch (AgentNotConnectedException ex)
        {
            Logger.LogError(ex, "Restore packages failed (agent not connected) for workspace {WorkspaceId}", WorkspaceId);
            SafeInvoke(() => ToastService.ShowError($"Restore failed. {ex.Message}"));
            throw;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Restore packages failed for workspace {WorkspaceId}", WorkspaceId);
            SafeInvoke(() => ToastService.ShowError($"Restore failed: {ex.Message}"));
            throw;
        }
    }

    private sealed record PushWithDependenciesModalState
    {
        public bool IsVisible { get; init; }
        public PushDependencyInfoForRepo? Info { get; init; }
        public IReadOnlySet<int>? RepoIdsToPush { get; init; }
        public int RepoId { get; init; }
        public string? BranchName { get; init; }
        public string? RepoName { get; init; }
    }
}
