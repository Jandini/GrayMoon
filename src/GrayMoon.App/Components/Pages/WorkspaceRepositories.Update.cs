using GrayMoon.Abstractions.Exceptions;
using GrayMoon.App.Models;
using GrayMoon.App.Services;
using GrayMoon.App.Services.Orchestration;

namespace GrayMoon.App.Components.Pages;

public sealed partial class WorkspaceRepositories
{
    private UpdateModalState _updateModal = new();
    private UpdateModalState _updateAndPushModal = new();
    private LevelOnlyUpdateAndPushModalState _levelOnlyUpdateAndPushModal = new();

    private string UpdateJobTitle =>
        _presentation.ShowDependencyUpdateActions ? "Updating dependencies..." : "Updating version files...";

    /// <summary>Update button click: get update plan; if no updates, toast; else show modal (single vs multi-level).</summary>
    private async Task OnUpdateClickAsync()
    {
        if (workspace == null || !HasRepositories || IsJobRunning)
            return;

        var allLinks = await GetAllLinksForOperationAsync();
        if (allLinks.All(wr => wr.IsOnTag))
        {
            ToastService.Show("All repositories are on tags; checkout a branch first.");
            return;
        }

        var (updatePlan, _) = await ScopedExecutor.ExecuteAsync<IWorkspaceUpdateOperations, (IReadOnlyList<SyncDependenciesRepoPayload> Payload, bool IsMultiLevel)>(
            svc => svc.GetUpdatePlanAsync(WorkspaceId, RequireSelectedContextId()));
        var repoIdsWithUpdates = updatePlan.Select(p => p.RepoId).ToHashSet();

        var reposOnDefault = allLinks
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
            SetLevelError(0, ex.Message);
            StateHasChanged();
        }
    }

    private void CloseUpdateModal()
    {
        _updateModal = _updateModal with { IsVisible = false };
        StateHasChanged();
    }

    private async Task OnUpdateProceedAsync((string? CommitMessage, bool IncludeDeps, bool RestorePackages) args)
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
        if (workspace == null || !HasRepositories || IsJobRunning)
            return Task.CompletedTask;

        StartPageJob(UpdateJobTitle, async (job, ct) =>
        {
            var updateResult = await ScopedExecutor.ExecuteAsync<IWorkspaceUpdateOperations, DependencyUpdateRunResult>(svc =>
                svc.UpdateAsync(
                    WorkspaceId, RequireSelectedContextId(),
                    ct,
                    job.ToOperationProgress(),
                    (repoId, msg) => SafeInvoke(() => SetRepositoryError(repoId, msg)),
                    (level, msg) => SafeInvoke(() => SetLevelError(level, msg)),
                    repoIdsToUpdate: null,
                    commitMessage: commitMessage,
                    includeDepsInCommitMessage: includeDepsInCommitMessage));

            if (updateResult.Success)
                SafeInvoke(() => ClearRepositoryErrorsFor(updateResult.SyncedRepoIds));
        }, new PageJobOptions
        {
            RefreshOnCancel = true,
            OnError = ex =>
            {
                Logger.LogError(ex, "Error updating dependencies for workspace {WorkspaceId}", WorkspaceId);
                SafeInvoke(() => SetLevelError(0, ex.Message));
            }
        });

        return Task.CompletedTask;
    }
    private IReadOnlySet<int>? _reviewedDefaultBranchRepoIds;


    private async Task OnUpdateAndPushClickAsync()
    {
        if (workspace == null || !HasRepositories || IsJobRunning)
            return;

        var allLinks = await GetAllLinksForOperationAsync();
        if (allLinks.All(wr => wr.IsOnTag))
        {
            ToastService.Show("All repositories are on tags; checkout a branch first.");
            return;
        }

        var (updatePlan, _) = await ScopedExecutor.ExecuteAsync<IWorkspaceUpdateOperations, (IReadOnlyList<SyncDependenciesRepoPayload> Payload, bool IsMultiLevel)>(
            svc => svc.GetUpdatePlanAsync(WorkspaceId, RequireSelectedContextId()));
        var repoIdsWithUpdates = updatePlan.Select(p => p.RepoId).ToHashSet();

        _reviewedDefaultBranchRepoIds = allLinks
            .Where(wr => !wr.IsOnTag
                && !string.IsNullOrWhiteSpace(wr.DefaultBranchName)
                && string.Equals(wr.BranchName, wr.DefaultBranchName, StringComparison.Ordinal))
            .Select(wr => wr.RepositoryId)
            .ToHashSet();

        var reposOnDefault = allLinks
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

    private async Task OnUpdateAndPushProceedAsync((string? CommitMessage, bool IncludeDeps, bool RestorePackages) args)
    {
        _updateAndPushModal = _updateAndPushModal with
        {
            IsVisible = false,
            LastCommitMessage = args.CommitMessage,
            LastIncludeDeps = args.IncludeDeps
        };
        await RunUpdateAndPushCoreAsync(args.CommitMessage, args.IncludeDeps, args.RestorePackages);
    }

    private async Task OnLevelOnlyUpdateAndPushClickAsync()
    {
        if (workspace == null || !HasRepositories || IsJobRunning)
            return;

        var level = lowestLevelNeedingWork;
        if (!level.HasValue)
        {
            ToastService.Show("No repositories need work.");
            return;
        }

        var allLinks = await GetAllLinksForOperationAsync();
        if (allLinks.Where(wr => !wr.IsOnTag && (wr.DependencyLevel ?? 0) <= level).All(wr => wr.IsOnTag))
        {
            ToastService.Show("All repositories at this level are on tags; checkout a branch first.");
            return;
        }

        var (updatePlan, _) = await ScopedExecutor.ExecuteAsync<IWorkspaceUpdateOperations, (IReadOnlyList<SyncDependenciesRepoPayload> Payload, bool IsMultiLevel)>(
            svc => svc.GetUpdatePlanAsync(WorkspaceId, RequireSelectedContextId()));
        var repoIdsWithUpdates = updatePlan.Select(p => p.RepoId).ToHashSet();

        _reviewedDefaultBranchRepoIds = allLinks
            .Where(wr => !wr.IsOnTag
                && (wr.DependencyLevel ?? 0) <= level
                && !string.IsNullOrWhiteSpace(wr.DefaultBranchName)
                && string.Equals(wr.BranchName, wr.DefaultBranchName, StringComparison.Ordinal))
            .Select(wr => wr.RepositoryId)
            .ToHashSet();

        var reposOnDefault = allLinks
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

    private async Task OnLevelOnlyUpdateAndPushProceedAsync((string? CommitMessage, bool IncludeDeps, bool RestorePackages) args)
    {
        var level = _levelOnlyUpdateAndPushModal.Level;
        _levelOnlyUpdateAndPushModal = _levelOnlyUpdateAndPushModal with
        {
            IsVisible = false,
            LastCommitMessage = args.CommitMessage,
            LastIncludeDeps = args.IncludeDeps
        };
        await RunLevelOnlyUpdateAndPushCoreAsync(level, args.CommitMessage, args.IncludeDeps, args.RestorePackages);
    }

    private Task RunLevelOnlyUpdateAndPushCoreAsync(int level, string? commitMessage = null, bool includeDepsInCommitMessage = true, bool restorePackages = false)
        => StartUpdateAndPushJob($"Updating Level {level}...", level, commitMessage, includeDepsInCommitMessage, restorePackages);

    private Task RunUpdateAndPushCoreAsync(string? commitMessage = null, bool includeDepsInCommitMessage = true, bool restorePackages = false)
        => StartUpdateAndPushJob(UpdateJobTitle, maxLevel: null, commitMessage, includeDepsInCommitMessage, restorePackages);

    /// <summary>
    /// Submits one explicit command to the shared combined Update and Push (<see cref="IWorkspaceUpdateAndPushOperations"/>);
    /// the page owns only the confirmations, the job and the presentation. The Feature context is captured here, once.
    /// </summary>
    private Task StartUpdateAndPushJob(string title, int? maxLevel, string? commitMessage, bool includeDepsInCommitMessage, bool restorePackages)
    {
        if (workspace == null || !HasRepositories || IsJobRunning)
            return Task.CompletedTask;

        // Taken when the user was warned (or found nothing to warn about); used once so a stale set never carries over.
        var reviewedDefaultBranchRepoIds = _reviewedDefaultBranchRepoIds;
        _reviewedDefaultBranchRepoIds = null;

        var command = new StartUpdateAndPush(
            WorkspaceId,
            RequireSelectedContextId(),
            maxLevel,
            commitMessage,
            includeDepsInCommitMessage,
            restorePackages,
            Origin: "RepositoriesPage",
            ReviewedDefaultBranchRepoIds: reviewedDefaultBranchRepoIds);
        JobService.StartJob(PageJobKey, title, (job, ct) => RunUpdateAndPushCommandAsync(job, ct, command));
        return Task.CompletedTask;
    }

    /// <summary>
    /// The confirmation is raised while the run that asked for it is still settling, and StartJob silently starts
    /// nothing while the lock is held. Waiting on the in-memory job state is free; the approval stays valid meanwhile.
    /// </summary>
    private async Task StartApprovedContinuationAsync(ApproveNormalPushContinuation approval)
    {
        for (var i = 0; i < 100 && IsJobRunning && !_disposed; i++)
            await Task.Delay(100);

        if (_disposed) return;
        if (IsJobRunning)
        {
            ToastService.ShowError("Another operation is still running. Run Update and Push again when it has finished.");
            return;
        }

        JobService.StartJob(PageJobKey, "Preparing push...", (j, c) => RunUpdateAndPushCommandAsync(j, c, approval));
    }

    private async Task RunUpdateAndPushCommandAsync(BackgroundJobHandle job, CancellationToken ct, UpdateAndPushCommand command)
    {
        try
        {
            var result = await ScopedExecutor.ExecuteAsync<IWorkspaceUpdateAndPushOperations, UpdateAndPushExecutionResult>(
                svc => svc.ExecuteAsync(
                    command,
                    new UpdateAndPushObserver(
                        job.ReportProgress,
                        (repoId, msg) => SafeInvoke(() => SetRepositoryError(repoId, msg)),
                        (errorLevel, msg) => SafeInvoke(() => SetLevelError(errorLevel, msg))),
                    ct));

            await ApplyUpdateAndPushExecutionResultAsync(result);

            var notice = UpdateAndPushResultPresenter.Present(result, errorsShownOnGrid: true);
            SafeInvoke(() =>
            {
                switch (notice.Kind)
                {
                    case UpdateAndPushNoticeKind.Info:
                        ToastService.Show(notice.Message);
                        break;
                    case UpdateAndPushNoticeKind.Error:
                        ToastService.ShowError(notice.Message);
                        break;
                    case UpdateAndPushNoticeKind.Approval:
                        var approval = notice.Approval!;
                        ShowConfirm(notice.Message, () => StartApprovedContinuationAsync(approval), confirmButtonText: "Continue");
                        break;
                }
            });
        }
        catch (OperationCanceledException)
        {
            await ReloadWorkspaceDataAfterCancelAsync();
            SafeInvoke(() => ToastService.Show("Push cancelled."));
            throw;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Update & Push failed for workspace {WorkspaceId}", WorkspaceId);
            SafeInvoke(() => SetLevelError(0, ex.Message));
            throw;
        }
        finally
        {
            await RefreshAfterPipelinedRunAsync();
        }
    }

    /// <summary>Reloads the grid after a combined run, shows what the run reports and clears the errors of repositories it updated cleanly.</summary>
    private async Task ApplyUpdateAndPushExecutionResultAsync(UpdateAndPushExecutionResult result)
    {
        await ReloadWorkspaceDataFromFreshScopeAsync();
        _ = InvokeAsync(() => { if (!_disposed) { ApplySyncStateFromLoadedItems(); StateHasChanged(); } });

        if (result.Status == UpdateAndPushStatus.Rejected)
            return;

        // The live callbacks reach the page only while it is alive and only as they happen; a push that waits
        // minutes for a package can fail long after the user looked away. The returned result is the complete
        // account, so apply it; the presenter's notice then says that the push stopped.
        if (result.Push is { Success: false } pushFailure)
        {
            SafeInvoke(() =>
            {
                ApplyRepositoryErrors(pushFailure.RepoErrors);
                ApplyLevelErrors(pushFailure.LevelErrors);
            });
        }

        if (result.Update is { Success: true } update)
        {
            // Errors the push lane reported while the update was still running must survive this clean-up.
            var pushFailedRepoIds = result.Push?.RepoErrors?.Keys.ToHashSet() ?? new HashSet<int>();
            SafeInvoke(() => ClearRepositoryErrorsFor(update.SyncedRepoIds.Where(id => !pushFailedRepoIds.Contains(id)).ToHashSet()));
        }
    }

    /// <summary>Reloads the grid after a pipelined Update and Push and clears the errors of repositories it updated cleanly.</summary>
    private async Task ApplyPipelinedUpdateAndPushResultAsync(UpdateAndPushResult result, string nothingToPushMessage)
    {
        await ReloadWorkspaceDataFromFreshScopeAsync();
        _ = InvokeAsync(() => { if (!_disposed) { ApplySyncStateFromLoadedItems(); StateHasChanged(); } });

        // The live callbacks reach the page only while it is alive and only as they happen; a push that waits
        // minutes for a package can fail long after the user looked away. The returned result is the complete
        // account (like the sequential push), so apply it and say that the push stopped.
        if (result.Push is { Success: false } pushFailure)
        {
            SafeInvoke(() =>
            {
                ApplyRepositoryErrors(pushFailure.RepoErrors);
                ApplyLevelErrors(pushFailure.LevelErrors);
                ToastService.Show(PushOperationResult.Describe(pushFailure));
            });
        }

        if (result.Update.Success)
        {
            // Errors the push lane reported while the update was still running must survive this clean-up.
            var pushFailedRepoIds = result.Push?.RepoErrors?.Keys.ToHashSet() ?? new HashSet<int>();
            SafeInvoke(() => ClearRepositoryErrorsFor(result.Update.SyncedRepoIds.Where(id => !pushFailedRepoIds.Contains(id)).ToHashSet()));
            if (result.Push?.Success != false && result.PushedRepoCount == 0)
                SafeInvoke(() => ToastService.Show(nothingToPushMessage));
        }
    }

    private async Task RefreshAfterPipelinedRunAsync()
    {
        _pendingRefreshAfterJob = false;
        await InvokeAsync(async () =>
        {
            if (_disposed) return;
            await RefreshFromSync();
        });
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
