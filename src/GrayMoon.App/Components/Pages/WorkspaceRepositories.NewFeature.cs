using GrayMoon.Abstractions.Exceptions;
using GrayMoon.App.Components.Modals;
using GrayMoon.App.Services;

namespace GrayMoon.App.Components.Pages;

public sealed partial class WorkspaceRepositories
{
    private NewFeatureModalState _newFeatureModal = new();

    private async Task ShowNewFeatureModalAsync()
    {
        if (workspace == null || workspaceRepositories.Count == 0)
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

    private Task HandleNewFeatureCreateAsync(NewFeatureRequest request)
    {
        if (workspace == null || IsJobRunning)
            return Task.CompletedTask;

        CloseNewFeatureModal();
        errorMessage = null;

        var tagFilteredRepoIds = request.SkipReposOnTags
            ? workspaceRepositories.Where(wr => !wr.IsOnTag).Select(wr => wr.RepositoryId).ToHashSet()
            : (IReadOnlySet<int>?)null;

        // Phases 1 + 2: branch creation (hooks suppressed, state persisted inline) then optional update.
        // NewFeatureOrchestrator guarantees all CheckedOutTag fields are null before the update
        // runs, so DependencyUpdateOrchestrator never skips previously tag-pinned repos.
        StartPageJob("Creating branches...", async (job, ct) =>
        {
            IReadOnlySet<int> syncedRepoIds = new HashSet<int>();
            try
            {
                syncedRepoIds = await ScopedExecutor.ExecuteAsync<NewFeatureOrchestrator, IReadOnlySet<int>>(
                    svc => svc.RunAsync(
                        WorkspaceId,
                        request.NewBranchName,
                        request.BaseBranch,
                        tagFilteredRepoIds,
                        request.UpdateDependencies,
                        commitMessage: null,
                        setProgress: msg => job.ReportProgress(msg),
                        reportBranchProgress: (completed, total) =>
                        {
                            job.ReportProgress($"Created {completed} of {total} branches");
                        },
                        setRepositoryError: (repoId, msg) => SafeInvoke(() => { repositoryErrors[repoId] = msg; }),
                        ct));

                // Unconditional reload so workspaceRepositories is current for Phase 3
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
                Logger.LogError(ex, "New Feature: orchestration failed for workspace {WorkspaceId}", WorkspaceId);
                SafeInvoke(() => ShowOperationError("New Feature Failed", "Could not complete the New Feature workflow. The GrayMoon Agent may be offline or a dependency update failed. Check individual repository errors for details."));
                throw;
            }

            if (!request.PushChanges)
                return;

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
                SafeInvoke(() => ShowOperationError("Push Failed", "Could not determine push plan. The GrayMoon Agent may be offline."));
                throw;
            }

            try
            {
                await ExecutePushCoreAsync(job, ct, pushRepoIds, synchronizedPush: true, requiredPackageIds, syncedRepoIds);
            }
            catch (SynchronizedPushNotPossibleException ex)
            {
                SafeInvoke(() => ShowOperationError("Push Failed",
                    $"Synchronized push could not complete: {ex.MissingPackagesCount} required package mapping(s) are missing. Check NuGet connector configuration and token, then retry."));
                return;
            }
        }, new PageJobOptions { RefreshOnSuccess = false });

        return Task.CompletedTask;
    }

    private sealed record NewFeatureModalState
    {
        public bool IsVisible { get; init; }
        public string? WorkspaceName { get; init; }
        public IReadOnlyList<string> CommonBranchNames { get; init; } = Array.Empty<string>();
        public string DefaultDisplayText { get; init; } = "multiple";
    }
}
