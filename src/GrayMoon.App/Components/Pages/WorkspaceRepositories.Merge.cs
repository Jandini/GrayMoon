using GrayMoon.App.Models;
using GrayMoon.App.Services;

namespace GrayMoon.App.Components.Pages;

/// <summary>
/// Merge confirmation dialog opened from the "merge" segment of the open-PR chip. Every piece of mergeability
/// data shown here (and the merge action itself) is fetched fresh from GitHub via WorkspacePullRequestService -
/// never derived from the polled/cached PR badge state - so GitHub remains the sole authority on whether and how
/// the merge can proceed.
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
            // Already known from the polled PR badge state - link the header immediately instead of waiting
            // for the fresh GitHub fetch below to populate Details.HtmlUrl.
            PrHtmlUrl = string.IsNullOrWhiteSpace(prInfo?.HtmlUrl) ? null : prInfo.HtmlUrl,
            IsLoading = true
        };
        StateHasChanged();

        PullRequestMergeDetails? details = null;
        try
        {
            details = await ScopedExecutor.ExecuteAsync<WorkspacePullRequestService, PullRequestMergeDetails?>(
                svc => svc.GetMergeDetailsAsync(WorkspaceId, link.RepositoryId, prNumber.Value));
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to load merge details for repo {RepositoryId}, PR #{PrNumber}", link.RepositoryId, prNumber.Value);
        }

        if (_disposed || !_mergePrModal.IsVisible || _mergePrModal.RepositoryId != link.RepositoryId)
            return;

        _mergePrModal = _mergePrModal with
        {
            IsLoading = false,
            Details = details,
            ErrorMessage = details == null ? "Could not load pull request details from GitHub." : null
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

    private async Task HandleMergeConfirmedAsync(MergeMethod method)
    {
        var repositoryId = _mergePrModal.RepositoryId;
        var prNumber = _mergePrModal.PrNumber;
        var headSha = _mergePrModal.Details?.HeadSha;
        if (repositoryId <= 0 || prNumber <= 0 || _mergePrModal.IsMerging)
            return;

        _mergePrModal = _mergePrModal with { IsMerging = true, ErrorMessage = null };
        StateHasChanged();

        MergeResult result;
        try
        {
            result = await ScopedExecutor.ExecuteAsync<WorkspacePullRequestService, MergeResult>(
                svc => svc.MergePullRequestAsync(WorkspaceId, repositoryId, prNumber, method, headSha));
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Merge PR failed for repo {RepositoryId}, PR #{PrNumber}", repositoryId, prNumber);
            result = new MergeResult(false, ex.Message);
        }

        if (_disposed)
            return;

        if (result.Success)
        {
            _mergePrModal = new MergePullRequestModalState();
            ToastService.Show($"Merged pull request #{prNumber}.");
            await RefreshFromSync();
        }
        else
        {
            _mergePrModal = _mergePrModal with { IsMerging = false, ErrorMessage = result.Message ?? "The merge could not be completed." };
        }
        StateHasChanged();
    }

    private sealed record MergePullRequestModalState
    {
        public bool IsVisible { get; init; }
        public int RepositoryId { get; init; }
        public int PrNumber { get; init; }
        /// <summary>PR HTML URL known immediately from the polled PR badge state, before Details.HtmlUrl loads.</summary>
        public string? PrHtmlUrl { get; init; }
        public bool IsLoading { get; init; }
        public bool IsMerging { get; init; }
        /// <summary>True while a push/pull triggered from the local-state row's commits text is running.</summary>
        public bool IsSyncingLocalState { get; init; }
        public PullRequestMergeDetails? Details { get; init; }
        public string? ErrorMessage { get; init; }
    }
}
