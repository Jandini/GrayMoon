using GrayMoon.Abstractions.Exceptions;
using GrayMoon.App.Components.Modals;
using GrayMoon.App.Services;

namespace GrayMoon.App.Components.Pages;

public sealed partial class WorkspaceRepositories
{
    private NewFeatureModalState _newFeatureModal = new();

    private async Task ShowNewFeatureModalAsync()
    {
        if (workspace == null || !HasRepositories)
            return;
        try
        {
            await LoadCommonBranchesForBranchModalAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Could not load common branches for new feature modal");
        }
        _newFeatureModal = _newFeatureModal with
        {
            IsVisible = true,
            WorkspaceName = workspace?.Name,
            CommonBranchNames = _branchModal.CommonBranchNames,
            DefaultDisplayText = _branchModal.DefaultDisplayText,
        };
        StateHasChanged();
    }

    private void CloseNewFeatureModal()
    {
        _newFeatureModal = _newFeatureModal with { IsVisible = false };
    }

    private async Task HandleNewFeatureCreateAsync(NewFeatureRequest request)
    {
        if (workspace == null || IsJobRunning)
            return;

        CloseNewFeatureModal();

        var allLinks = await GetAllLinksForOperationAsync();
        var tagFilteredRepoIds = request.SkipReposOnTags
            ? allLinks.Where(wr => !wr.IsOnTag).Select(wr => wr.RepositoryId).ToHashSet()
            : (IReadOnlySet<int>?)null;

        // Phases 1 + 2: branch creation (hooks suppressed, state persisted inline) then optional update.
        // NewFeatureOrchestrator guarantees all CheckedOutTag fields are null before the update
        // runs, so DependencyUpdateOrchestrator never skips previously tag-pinned repos.
        StartPageJob("Creating branches...", async (job, ct) =>
        {
            IReadOnlySet<int> syncedRepoIds = new HashSet<int>();
            try
            {
                var updateResult = await ScopedExecutor.ExecuteAsync<IWorkspaceFeatureOperations, DependencyUpdateRunResult>(
                    svc => svc.CreateAsync(
                        WorkspaceId,
                        request.NewBranchName,
                        request.BaseBranch,
                        tagFilteredRepoIds,
                        request.UpdateDependencies,
                        commitMessage: null,
                        progress: job.ToOperationProgress(),
                        setRepositoryError: (repoId, msg) => SafeInvoke(() => SetRepositoryError(repoId, msg)),
                        setLevelError: (level, msg) => SafeInvoke(() => SetLevelError(level, msg)),
                        cancellationToken: ct));

                // Unconditional reload so workspaceRepositories is current for Phase 3
                await ReloadWorkspaceDataFromFreshScopeAsync();
                _ = InvokeAsync(() => { if (!_disposed) { ApplySyncStateFromLoadedItems(); StateHasChanged(); } });

                if (!updateResult.ShouldChainPush(request.PushChanges))
                    return;

                syncedRepoIds = updateResult.SyncedRepoIds;
            }
            catch (OperationCanceledException)
            {
                await ReloadWorkspaceDataAfterCancelAsync();
                throw;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "New Feature: orchestration failed for workspace {WorkspaceId}", WorkspaceId);
                SafeInvoke(() => SetLevelError(0, ex.Message));
                throw;
            }

            // Phase 3: determine push plan and execute push (per-level restore handled inside push service)
            job.ReportProgress("Preparing push...");
            IReadOnlySet<int> pushRepoIds;
            IReadOnlySet<string> requiredPackageIds;
            try
            {
                var plan = await BuildPushPlanAsync("No repositories to push.", ct);
                if (plan == null) return;
                (pushRepoIds, requiredPackageIds) = plan.Value;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "New Feature: failed to get push plan for workspace {WorkspaceId}", WorkspaceId);
                SafeInvoke(() => SetLevelError(0, ex.Message));
                throw;
            }

            try
            {
                await ExecutePushCoreAsync(job, ct, pushRepoIds, synchronizedPush: true, requiredPackageIds, syncedRepoIds);
            }
            catch (SynchronizedPushNotPossibleException ex)
            {
                Logger.LogError(ex, "New Feature: synchronized push not possible for workspace {WorkspaceId}", WorkspaceId);
                SafeInvoke(() => SetLevelError(0, ex.Message));
                return;
            }
        }, new PageJobOptions { RefreshOnSuccess = false });
    }

    private sealed record NewFeatureModalState
    {
        public bool IsVisible { get; init; }
        public string? WorkspaceName { get; init; }
        public IReadOnlyList<string> CommonBranchNames { get; init; } = Array.Empty<string>();
        public string DefaultDisplayText { get; init; } = "multiple";
    }
}
