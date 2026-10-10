using GrayMoon.Abstractions.Exceptions;
using GrayMoon.App.Components.Modals;
using GrayMoon.App.Services;

namespace GrayMoon.App.Components.Pages;

public sealed partial class WorkspaceRepositories
{
    private PrepareWorkspaceModalState _prepareWorkspaceModal = new();

    private async Task ShowPrepareWorkspaceModalAsync()
    {
        if (workspace == null || !HasRepositories)
            return;
        try
        {
            await LoadCommonBranchesForBranchModalAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Could not load common branches for prepare workspace modal");
        }
        _prepareWorkspaceModal = _prepareWorkspaceModal with
        {
            IsVisible = true,
            WorkspaceName = workspace?.Name,
            CommonBranchNames = _branchModal.CommonBranchNames,
            DefaultDisplayText = _branchModal.DefaultDisplayText,
        };
        StateHasChanged();
    }

    private void ClosePrepareWorkspaceModal()
    {
        _prepareWorkspaceModal = _prepareWorkspaceModal with { IsVisible = false };
    }

    private async Task HandlePrepareWorkspaceAsync(PrepareWorkspaceRequest request)
    {
        if (workspace == null || IsJobRunning)
            return;

        ClosePrepareWorkspaceModal();

        var allLinks = await GetAllLinksForOperationAsync();
        var tagFilteredRepoIds = request.SkipReposOnTags
            ? allLinks.Where(wr => !wr.IsOnTag).Select(wr => wr.RepositoryId).ToHashSet()
            : (IReadOnlySet<int>?)null;

        // Branch creation (hooks suppressed, state persisted inline), then - only when every targeted repository is on
        // the new branch - the update, the push, or both as the two-lane Update and Push. A branch failure stops the
        // orchestrator before anything else runs and is reported through the error callbacks.
        StartPageJob("Creating branches...", async (job, ct) =>
        {
            IReadOnlySet<int> syncedRepoIds = new HashSet<int>();
            var pipelined = false;
            try
            {
                var result = await ScopedExecutor.ExecuteAsync<IWorkspacePreparationOperations, PrepareWorkspaceResult>(
                    svc => svc.PrepareAsync(
                        WorkspaceId, RequireSelectedContextId(),
                        request.NewBranchName,
                        request.BaseBranch,
                        tagFilteredRepoIds,
                        request.UpdateDependencies && _presentation.ShowDependencyUpdateActions,
                        request.PushChanges,
                        commitMessage: null,
                        progress: new SynchronousProgress(job.ReportProgress),
                        setRepositoryError: (repoId, msg) => SafeInvoke(() => SetRepositoryError(repoId, msg)),
                        setLevelError: (level, msg) => SafeInvoke(() => SetLevelError(level, msg)),
                        cancellationToken: ct));

                if (result.Pipeline is { } pipeline)
                {
                    pipelined = true;
                    await ApplyPipelinedUpdateAndPushResultAsync(pipeline, "Workspace prepared. Nothing to push.");
                    return;
                }

                // Unconditional reload so the grid shows the branches actually created (even after a branch failure)
                // and workspaceRepositories is current for the push below.
                await ReloadWorkspaceDataFromFreshScopeAsync();
                _ = InvokeAsync(() => { if (!_disposed) { ApplySyncStateFromLoadedItems(); StateHasChanged(); } });

                if (!result.BranchesCreated)
                    return;

                if (result.Success)
                    SafeInvoke(() => ClearRepositoryErrorsFor(result.SyncedRepoIds));

                if (!result.PushPending)
                    return;

                syncedRepoIds = result.SyncedRepoIds;
            }
            catch (OperationCanceledException)
            {
                await ReloadWorkspaceDataAfterCancelAsync();
                throw;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Prepare Workspace: orchestration failed for workspace {WorkspaceId}", WorkspaceId);
                SafeInvoke(() => SetLevelError(0, ex.Message));
                await ReloadWorkspaceDataAfterCancelAsync();
                throw;
            }
            finally
            {
                if (pipelined)
                    await RefreshAfterPipelinedRunAsync();
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
                Logger.LogError(ex, "Prepare Workspace: failed to get push plan for workspace {WorkspaceId}", WorkspaceId);
                SafeInvoke(() => SetLevelError(0, ex.Message));
                throw;
            }

            try
            {
                await ExecutePushCoreAsync(job, ct, pushRepoIds, synchronizedPush: true, requiredPackageIds, syncedRepoIds);
            }
            catch (SynchronizedPushNotPossibleException ex)
            {
                Logger.LogError(ex, "Prepare Workspace: synchronized push not possible for workspace {WorkspaceId}", WorkspaceId);
                SafeInvoke(() => SetLevelError(0, ex.Message));
                return;
            }
        }, new PageJobOptions { RefreshOnSuccess = false });
    }

    private sealed record PrepareWorkspaceModalState
    {
        public bool IsVisible { get; init; }
        public string? WorkspaceName { get; init; }
        public IReadOnlyList<string> CommonBranchNames { get; init; } = Array.Empty<string>();
        public string DefaultDisplayText { get; init; } = "multiple";
    }
}
