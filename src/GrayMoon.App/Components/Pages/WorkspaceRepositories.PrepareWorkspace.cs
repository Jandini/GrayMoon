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
        // the new branch - the update, the push, or both as the shared combined Update and Push. A branch failure stops the
        // orchestrator before anything else runs and is reported through the error callbacks.
        var jobContextId = RequireSelectedContextId();
        StartPageJob("Creating branches...", async (job, ct) =>
        {
            IReadOnlySet<int> syncedRepoIds = new HashSet<int>();
            var pipelined = false;
            try
            {
                var result = await ScopedExecutor.ExecuteAsync<IWorkspacePreparationOperations, PrepareWorkspaceResult>(
                    svc => svc.PrepareAsync(
                        WorkspaceId, jobContextId,
                        request.NewBranchName,
                        request.BaseBranch,
                        tagFilteredRepoIds,
                        request.UpdateDependencies && _presentation.ShowDependencyUpdateActions,
                        request.PushChanges,
                        request.RestorePackages,
                        commitMessage: null,
                        progress: new SynchronousProgress(job.ReportProgress),
                        setRepositoryError: (repoId, msg) => SafeInvoke(() => SetRepositoryError(repoId, msg)),
                        setLevelError: (level, msg) => SafeInvoke(() => SetLevelError(level, msg)),
                        cancellationToken: ct));

                if (result.Combined is { } combined)
                {
                    pipelined = true;
                    await ApplyUpdateAndPushExecutionResultAsync(combined);
                    PresentUpdateAndPushNotice(combined);
                    // After the notice, so a run that worked still says the branch was not created here.
                    if (combined.Success)
                        ShowBranchAlreadyExisted(request.NewBranchName, result);
                    return;
                }

                // Unconditional reload so the grid shows the branches actually created (even after a branch failure)
                // and workspaceRepositories is current for the push below.
                await ReloadWorkspaceDataFromFreshScopeAsync();
                _ = InvokeAsync(() => { if (!_disposed) { ApplySyncStateFromLoadedItems(); StateHasChanged(); } });

                if (!result.BranchesCreated)
                    return;

                if (result.Success)
                {
                    SafeInvoke(() => ClearRepositoryErrorsFor(result.SyncedRepoIds));
                    // A branch that was already there is a warning; the repositories left out are information only.
                    if (!ShowBranchAlreadyExisted(request.NewBranchName, result))
                        ShowSkippedRepositories(result.Skipped);
                }

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

            await PreparePushOnlyAsync(jobContextId, job, ct, syncedRepoIds, request.RestorePackages);
        }, new PageJobOptions { RefreshOnSuccess = false });
    }

    /// <summary>
    /// Warns that the branch was already there in some repositories and was checked out as it is. Returns false when
    /// every branch was created.
    /// </summary>
    private bool ShowBranchAlreadyExisted(string newBranchName, PrepareWorkspaceResult result)
    {
        if (BranchAlreadyExistedWarning.Describe(newBranchName, result.BranchAlreadyExisted) is not { } warning)
            return false;

        SafeInvoke(() => ToastService.ShowWarning(warning));
        return true;
    }

    /// <summary>
    /// Prepare Workspace with Push only: the standalone synchronized push after the branches exist. Update and Push never
    /// reaches here - it runs as the shared combined operation, including its approval for a normal push.
    /// <paramref name="restorePackages"/> is the dialog's Restore packages choice.
    /// </summary>
    private async Task PreparePushOnlyAsync(GrayMoon.Application.Features.WorkspaceFeatureContextId jobContextId, BackgroundJobHandle job, CancellationToken ct, IReadOnlySet<int> syncedRepoIds, bool restorePackages)
    {
        // Phase 3: determine push plan and execute push. A local restore runs only when the dialog asked for one.
        job.ReportProgress("Preparing push...");
        IReadOnlySet<int> pushRepoIds;
        IReadOnlySet<string> requiredPackageIds;
        try
        {
            var plan = await BuildPushPlanAsync(jobContextId, "No repositories to push.", ct, progress: job.ToOperationProgress());
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
            await ExecutePushCoreAsync(jobContextId, job, ct, pushRepoIds, synchronizedPush: true, requiredPackageIds, syncedRepoIds, restorePackages: restorePackages);
        }
        catch (SynchronizedPushNotPossibleException ex)
        {
            Logger.LogError(ex, "Prepare Workspace: synchronized push not possible for workspace {WorkspaceId}", WorkspaceId);
            SafeInvoke(() => SetLevelError(0, ex.Message));
            return;
        }
    }

    private sealed record PrepareWorkspaceModalState
    {
        public bool IsVisible { get; init; }
        public string? WorkspaceName { get; init; }
        public IReadOnlyList<string> CommonBranchNames { get; init; } = Array.Empty<string>();
        public string DefaultDisplayText { get; init; } = "multiple";
    }
}
