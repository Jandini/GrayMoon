using GrayMoon.App.Components.Modals;
using GrayMoon.App.Models;
using GrayMoon.App.Services;

namespace GrayMoon.App.Components.Pages;

/// <summary>
/// Merge confirmation dialog opened from the "merge" segment of the open-PR chip. Every piece of mergeability
/// data shown here (and the merge action itself) is fetched fresh from GitHub via WorkspacePullRequestService -
/// never derived from the polled/cached PR badge state - so GitHub remains the sole authority on whether and how
/// the merge can proceed. The initial open is split into a fast snapshot phase (title/branches/conflicts, seeded
/// from the polled PR badge state and then confirmed by an ETag-cheap GitHub call) and a background review-details
/// phase (reviews/checks/allowed methods), so the dialog renders progressively instead of behind one blocking spinner.
/// </summary>
public sealed partial class WorkspaceRepositories
{
    private MergePullRequestModalState _mergePrModal = new();

    private async Task OpenMergeDialogAsync(WorkspaceRepositoryLink link)
    {
        var prInfo = GetPrInfoForRepository(link.RepositoryId);
        var prNumber = prInfo?.Number;
        if (prNumber is not > 0)
            return;

        _mergePrModal = new MergePullRequestModalState
        {
            IsVisible = true,
            RepositoryId = link.RepositoryId,
            PrNumber = prNumber.Value,
            // Already known from the polled PR badge state - link the header immediately and seed the Conflicts
            // row so it doesn't render blank while the (now ETag-cheap) snapshot call below confirms it.
            PrHtmlUrl = string.IsNullOrWhiteSpace(prInfo?.HtmlUrl) ? null : prInfo.HtmlUrl,
            Details = new PullRequestMergeDetails
            {
                Number = prNumber.Value,
                HtmlUrl = prInfo?.HtmlUrl ?? string.Empty,
                ChangedFiles = prInfo?.ChangedFiles,
                Mergeable = prInfo?.Mergeable,
                MergeableState = prInfo?.MergeableState
            },
            IsLoading = true,
            IsLoadingReviewDetails = true
        };
        StateHasChanged();

        PullRequestMergeSnapshot? snapshot = null;
        try
        {
            snapshot = await ScopedExecutor.ExecuteAsync<WorkspacePullRequestService, PullRequestMergeSnapshot?>(
                svc => svc.GetMergeSnapshotAsync(WorkspaceId, link.RepositoryId, prNumber.Value));
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to load merge snapshot for repo {RepositoryId}, PR #{PrNumber}", link.RepositoryId, prNumber.Value);
        }

        if (_disposed || !_mergePrModal.IsVisible || _mergePrModal.RepositoryId != link.RepositoryId)
            return;

        _mergePrModal = _mergePrModal with
        {
            IsLoading = false,
            Details = snapshot == null
                ? _mergePrModal.Details
                : _mergePrModal.Details! with
                {
                    Number = snapshot.Number,
                    Title = snapshot.Title,
                    HeadRef = snapshot.HeadRef,
                    BaseRef = snapshot.BaseRef,
                    HeadSha = snapshot.HeadSha,
                    HtmlUrl = snapshot.HtmlUrl,
                    ChangedFiles = snapshot.ChangedFiles,
                    Mergeable = snapshot.Mergeable,
                    MergeableState = snapshot.MergeableState
                },
            ErrorMessage = snapshot == null ? "Could not load pull request details from GitHub." : null
        };
        StateHasChanged();

        if (snapshot == null)
        {
            _mergePrModal = _mergePrModal with { IsLoadingReviewDetails = false };
            StateHasChanged();
            return;
        }

        PullRequestMergeReviewDetails? review = null;
        try
        {
            review = await ScopedExecutor.ExecuteAsync<WorkspacePullRequestService, PullRequestMergeReviewDetails?>(
                svc => svc.GetMergeReviewDetailsAsync(WorkspaceId, link.RepositoryId, prNumber.Value, snapshot.HeadSha));
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to load merge review details for repo {RepositoryId}, PR #{PrNumber}", link.RepositoryId, prNumber.Value);
        }

        if (_disposed || !_mergePrModal.IsVisible || _mergePrModal.RepositoryId != link.RepositoryId)
            return;

        _mergePrModal = _mergePrModal with
        {
            IsLoadingReviewDetails = false,
            Details = review == null || _mergePrModal.Details == null
                ? _mergePrModal.Details
                : _mergePrModal.Details with
                {
                    ApprovedCount = review.ApprovedCount,
                    ChangesRequestedCount = review.ChangesRequestedCount,
                    OutstandingReviewers = review.OutstandingReviewers,
                    ApprovedByUsers = review.ApprovedByUsers,
                    Checks = review.Checks,
                    AllowedMergeMethods = review.AllowedMergeMethods,
                    DefaultMergeMethod = review.DefaultMergeMethod,
                    HasUncommittedChanges = review.HasUncommittedChanges,
                    UncommittedChangesCount = review.UncommittedChangesCount,
                    UnpushedCommitsCount = review.UnpushedCommitsCount,
                    IncomingCommitsCount = review.IncomingCommitsCount
                },
            ErrorMessage = review == null ? "Could not load review/check status from GitHub." : null
        };
        StateHasChanged();
    }

    private void CloseMergeModal()
    {
        _mergePrModal = _mergePrModal with { IsVisible = false };
        StateHasChanged();
    }

    /// <summary>
    /// Local-state row's commits text, "unpushed only" case: keeps the dialog open, runs the same push flow as the
    /// grid's CommitsBadge (dependency-aware push, default-branch warning included) for this one repository, then
    /// refreshes the dialog's own details once the underlying page job finishes.
    /// </summary>
    private Task HandleMergeDialogPushRequestedAsync()
    {
        var repositoryId = _mergePrModal.RepositoryId;
        var branchName = _mergePrModal.Details?.HeadRef;
        if (repositoryId <= 0)
            return Task.CompletedTask;
        return RunMergeDialogLocalActionAsync(() => OnPushBadgeClickAsync(repositoryId, branchName));
    }

    /// <summary>
    /// Local-state row's commits text when incoming commits exist (pull-only or pull+push sync): keeps the dialog
    /// open, runs the same commit-sync flow as the grid's CommitsBadge for this one repository, then refreshes the
    /// dialog's own details once the underlying page job finishes.
    /// </summary>
    private Task HandleMergeDialogPullRequestedAsync()
    {
        var repositoryId = _mergePrModal.RepositoryId;
        if (repositoryId <= 0)
            return Task.CompletedTask;
        return RunMergeDialogLocalActionAsync(() =>
        {
            OnPullBadgeClickAsync(repositoryId);
            return Task.CompletedTask;
        });
    }

    /// <summary>
    /// Runs a push/pull action that is itself implemented as a page-level background job (StartPageJob/JobService).
    /// The dialog stays mounted and visible the whole time - MergePullRequestModal drops its own z-index below
    /// BackgroundJobOverlay's LoadingOverlay while IsSyncingLocalState, so that overlay visually covers the dialog
    /// instead of the dialog itself disappearing and reappearing. Details refresh in place once the job finishes.
    /// </summary>
    private async Task RunMergeDialogLocalActionAsync(Func<Task> action)
    {
        if (_mergePrModal.IsSyncingLocalState)
            return;

        var repositoryId = _mergePrModal.RepositoryId;
        var prNumber = _mergePrModal.PrNumber;

        _mergePrModal = _mergePrModal with { IsSyncingLocalState = true };
        StateHasChanged();

        try
        {
            await action();
            await WaitForPageJobToFinishAsync();
        }
        finally
        {
            if (!_disposed && _mergePrModal.IsVisible && _mergePrModal.RepositoryId == repositoryId)
                await RefreshMergeDialogDetailsAsync(repositoryId, prNumber);
        }
    }

    /// <summary>
    /// Waits for the page job (StartPageJob/JobService, keyed by PageJobKey) to reach a terminal state. The push/
    /// commit-sync handlers this feeds from start their job synchronously before their triggering method returns,
    /// so by the time this is called the job (if any) already exists and is Running.
    /// </summary>
    private Task WaitForPageJobToFinishAsync()
    {
        var tcs = new TaskCompletionSource();

        void OnChanged()
        {
            var job = JobService.GetJob(PageJobKey);
            if (job == null || job.State != BackgroundJobState.Running)
                tcs.TrySetResult();
        }

        JobService.Changed += OnChanged;
        OnChanged();

        return tcs.Task.ContinueWith(_ => JobService.Changed -= OnChanged, TaskScheduler.Default);
    }

    /// <summary>
    /// Called from the background PR polling loop (WorkspaceRepositories.PrPolling.cs) on every tick so an open
    /// merge dialog's checks/reviews/mergeability - e.g. "2 checks still running" - stay in step with the same
    /// GitHub state that just updated the row's PR badge, instead of going stale the moment the dialog opens.
    /// Skipped while the dialog is closed, loading its first fetch, mid-merge, or mid local sync so this never
    /// races those other in-flight updates to the same state.
    /// </summary>
    private Task RefreshOpenMergeDialogIfDueAsync()
    {
        if (_disposed || !_mergePrModal.IsVisible || _mergePrModal.IsLoading || _mergePrModal.IsLoadingReviewDetails || _mergePrModal.IsMerging || _mergePrModal.IsSyncingLocalState)
            return Task.CompletedTask;

        return RefreshMergeDialogDetailsAsync(_mergePrModal.RepositoryId, _mergePrModal.PrNumber);
    }

    /// <summary>Re-fetches merge details in place after a push/pull triggered from within the (still-open) dialog completes.</summary>
    private async Task RefreshMergeDialogDetailsAsync(int repositoryId, int prNumber)
    {
        PullRequestMergeDetails? details = null;
        try
        {
            details = await ScopedExecutor.ExecuteAsync<WorkspacePullRequestService, PullRequestMergeDetails?>(
                svc => svc.GetMergeDetailsAsync(WorkspaceId, repositoryId, prNumber));
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to refresh merge details after local sync action for repo {RepositoryId}, PR #{PrNumber}", repositoryId, prNumber);
        }

        // If the user closed the dialog mid-sync, IsVisible is already false - leave it closed rather than
        // reopening it once the background job finishes.
        if (_disposed || !_mergePrModal.IsVisible || _mergePrModal.RepositoryId != repositoryId)
            return;

        _mergePrModal = _mergePrModal with
        {
            IsSyncingLocalState = false,
            Details = details ?? _mergePrModal.Details,
            ErrorMessage = details == null ? _mergePrModal.ErrorMessage : null
        };
        StateHasChanged();
    }

    /// <summary>
    /// Entry point for the modal's merge button. When the button is rendering as the orange "proceed with caution"
    /// warning (<see cref="PullRequestMergeDetails.HasMergeWarning"/> - uncommitted changes and/or unpushed/incoming
    /// commits in the local clone), interrupts with a confirm dialog that names the specific reason(s) before
    /// continuing on to <see cref="HandleMergeRequestedAsync"/> (which may itself confirm sync-to-default), since
    /// that local state would otherwise silently not be reflected in the merge. Proceeds straight through when
    /// there's no warning.
    /// </summary>
    private Task HandleMergeButtonClickedAsync(MergePullRequestChoice choice)
    {
        var details = _mergePrModal.Details;
        if (details == null || _mergePrModal.IsMerging)
            return Task.CompletedTask;

        if (!details.HasMergeWarning)
            return HandleMergeRequestedAsync(choice);

        ShowConfirm(
            $"This branch has {BuildMergeWarningReason(details)}, which will not be reflected in the merge.\nMerge anyway?",
            () => HandleMergeRequestedAsync(choice),
            "Merge anyway");
        return Task.CompletedTask;
    }

    /// <summary>Human-readable list of why the local-state row is flagged, e.g. "2 uncommitted changes and 1 unpushed commit".</summary>
    private static string BuildMergeWarningReason(PullRequestMergeDetails details)
    {
        var parts = new List<string>();
        if (details.UncommittedChangesCount > 0)
            parts.Add($"{details.UncommittedChangesCount} uncommitted change{(details.UncommittedChangesCount == 1 ? "" : "s")}");
        if (details.UnpushedCommitsCount > 0)
            parts.Add($"{details.UnpushedCommitsCount} unpushed commit{(details.UnpushedCommitsCount == 1 ? "" : "s")}");
        if (details.IncomingCommitsCount > 0)
            parts.Add($"{details.IncomingCommitsCount} incoming commit{(details.IncomingCommitsCount == 1 ? "" : "s")}");

        if (parts.Count == 0)
            return "unsynced local work";
        if (parts.Count == 1)
            return parts[0];
        return string.Join(", ", parts.Take(parts.Count - 1)) + " and " + parts[^1];
    }

    private Task HandleMergeRequestedAsync(MergePullRequestChoice choice)
    {
        if (choice.SyncToDefault)
        {
            var prNumber = _mergePrModal.PrNumber;
            ShowConfirm(
                $"Merge pull request #{prNumber} and then sync to the default branch?\n\nThis will checkout the default branch, remove the current branch locally, and pull the latest.",
                () => ExecuteMergeAsync(choice.Method, syncToDefault: true),
                "Merge");
            return Task.CompletedTask;
        }

        return ExecuteMergeAsync(choice.Method, syncToDefault: false);
    }

    /// <summary>
    /// Runs the merge (and optional sync-to-default) as a single page job so the standard full-page LoadingOverlay
    /// covers both steps back-to-back without ever hiding and reappearing in between - the modal itself stays
    /// mounted throughout (see IsMerging's z-index handling in MergePullRequestModal) rather than closing the
    /// instant the merge call returns. "Merged 1 of 1 pull requests…" is written for a single PR today but keeps
    /// the same completed/total shape a future multi-PR merge would report incrementally through job.ReportProgress.
    /// </summary>
    private Task ExecuteMergeAsync(MergeMethod method, bool syncToDefault)
    {
        var repositoryId = _mergePrModal.RepositoryId;
        var prNumber = _mergePrModal.PrNumber;
        var headSha = _mergePrModal.Details?.HeadSha;
        if (repositoryId <= 0 || prNumber <= 0 || _mergePrModal.IsMerging || IsJobRunning)
            return Task.CompletedTask;

        _mergePrModal = _mergePrModal with { IsMerging = true, ErrorMessage = null };
        StateHasChanged();

        StartPageJob("Merging pull requests…", async (job, ct) =>
        {
            try
            {
                MergeResult result;
                try
                {
                    result = await ScopedExecutor.ExecuteAsync<IWorkspacePullRequestOperations, MergeResult>(
                        svc => svc.MergeAsync(WorkspaceId, repositoryId, prNumber, method, headSha, ct));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Logger.LogError(ex, "Merge PR failed for repo {RepositoryId}, PR #{PrNumber}", repositoryId, prNumber);
                    result = new MergeResult(false, ex.Message);
                }

                if (_disposed)
                    return;

                if (!result.Success)
                {
                    SafeInvoke(() =>
                    {
                        _mergePrModal = _mergePrModal with { IsMerging = false, ErrorMessage = result.Message ?? "The merge could not be completed." };
                        StateHasChanged();
                    });
                    return;
                }

                job.ReportProgress("Merged 1 of 1 pull requests…");
                SafeInvoke(() =>
                {
                    _mergePrModal = new MergePullRequestModalState();
                    ToastService.Show($"Merged pull request #{prNumber}.");
                    StateHasChanged();
                });

                if (syncToDefault)
                {
                    var syncResult = await ScopedExecutor.ExecuteAsync<IWorkspaceSyncOperations, UnattendedSyncToDefaultResult>(
                        svc => svc.SyncToDefaultAsync(WorkspaceId, [repositoryId], job.ToOperationProgress(), ct));

                    if (!syncResult.Completed && syncResult.AbortReason != null)
                        SafeInvoke(() => SetRepositoryError(repositoryId, syncResult.AbortReason));
                }

                await InvokeAsync(async () =>
                {
                    if (_disposed) return;
                    await RefreshFromSync();
                });
            }
            catch (OperationCanceledException)
            {
                // The merge call itself has no cancellation checkpoint until it completes - cancelling here only
                // ever interrupts the optional sync-to-default step, never a half-completed merge.
                SafeInvoke(() =>
                {
                    _mergePrModal = _mergePrModal with { IsMerging = false };
                    StateHasChanged();
                });
                throw;
            }
        }, new PageJobOptions
        {
            RefreshOnSuccess = false,
            CancelToast = "Merge cancelled.",
            OnError = ex => Logger.LogError(ex, "Merge pull request job failed for repo {RepositoryId}, PR #{PrNumber}", repositoryId, prNumber)
        });

        return Task.CompletedTask;
    }

    private sealed record MergePullRequestModalState
    {
        public bool IsVisible { get; init; }
        public int RepositoryId { get; init; }
        public int PrNumber { get; init; }
        /// <summary>PR HTML URL known immediately from the polled PR badge state, before Details.HtmlUrl loads.</summary>
        public string? PrHtmlUrl { get; init; }
        public bool IsLoading { get; init; }
        /// <summary>True while the background review-details phase (reviews/checks/allowed methods/local-state) is in flight. The snapshot phase (title/branches/conflicts) has already completed by then, so the dialog stays visible - only the Review/Checks/local-state rows and the merge button show a loading state.</summary>
        public bool IsLoadingReviewDetails { get; init; }
        public bool IsMerging { get; init; }
        /// <summary>True while a push/pull triggered from the local-state row's commits text is running.</summary>
        public bool IsSyncingLocalState { get; init; }
        public PullRequestMergeDetails? Details { get; init; }
        public string? ErrorMessage { get; init; }
    }
}
