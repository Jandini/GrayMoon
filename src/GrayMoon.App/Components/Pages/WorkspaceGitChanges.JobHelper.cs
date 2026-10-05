using GrayMoon.App.Services;
using GrayMoon.App.Services.GitChanges;

namespace GrayMoon.App.Components.Pages;

public sealed partial class WorkspaceGitChanges
{
    private sealed record PageJobOptions
    {
        /// <summary>When true (default), calls LoadAsync inside InvokeAsync after work completes without exception.</summary>
        public bool ReloadOnSuccess { get; init; } = true;
        /// <summary>Toast message shown via ToastService.Show on OperationCanceledException. Null means no toast.</summary>
        public string? CancelToast { get; init; }
        /// <summary>Called with the exception on a general Exception catch. Null means no callback.</summary>
        public Action<Exception>? OnError { get; init; }
    }

    private string PageJobKey => new Uri(NavigationManager.Uri).AbsolutePath.ToLowerInvariant();

    /// <summary>
    /// Sibling of <see cref="PageJobKey"/> that does not match the URL path, so BackgroundJobOverlay
    /// never shows LoadingOverlay for any Git Changes status rescan (empty-state, on-open warm-up, or
    /// manual Refresh). Shared with Repositories via <see cref="WorkspaceJobKeys.GitChangesScanKey"/>
    /// so StartJob idempotency coalesces overlapping requests.
    /// </summary>
    private string ScanJobKey => WorkspaceJobKeys.GitChangesScanKey(WorkspaceId);

    /// <summary>
    /// True only when THIS page's overlay mutation job is actually running under <see cref="PageJobKey"/>
    /// (commit, bulk stage/unstage/discard, etc.).
    /// Uses <see cref="IBackgroundJobService.GetJob"/> so a Repositories Push Updated / Sync on the same
    /// workspace (different overlay key) does NOT count.
    /// Feature-selector disable binds here - not to <see cref="IsJobRunning"/>.
    /// </summary>
    private bool IsOwnPageJobRunning =>
        JobService.GetJob(PageJobKey) is { State: BackgroundJobState.Running };

    /// <summary>
    /// True when any process-wide workspace mutation is in flight for this workspace: this page's own
    /// overlay job OR a Repositories Push/Update/Sync/etc. (via <see cref="IBackgroundJobService.IsRunning"/>
    /// falling through to <c>IWorkspaceOperationRunner.IsBusy</c>).
    /// Use this to block starting another mutating action (commit buttons, stage-all, discard-all).
    /// Do NOT use this to disable the Feature selector - that falsely locks the selector while Push
    /// Updated runs on Repositories even when Changes is idle / showing "No changes".
    /// </summary>
    private bool IsJobRunning => JobService.IsRunning(PageJobKey);

    /// <summary>
    /// True while a Git Changes status scan (<c>:scan</c> key) is running - warm-up, empty-state
    /// Refresh, or header Refresh. Scans are read-only git-status work; they do not disable the
    /// Feature selector (switching aborts the scan instead - see <see cref="OnSelectedContextChangedAsync"/>).
    /// </summary>
    private bool IsScanRunning => JobService.IsRunning(ScanJobKey);

    /// <summary>
    /// Local Changes work owned by this page: own overlay mutation OR a status scan.
    /// Used for refresh coalescing and line-stats warm-up gating - never for Feature-selector disable
    /// (that is <see cref="IsOwnPageJobRunning"/> only).
    /// </summary>
    private bool IsLocalGitChangesWorkRunning => IsOwnPageJobRunning || IsScanRunning;

    private string? ScanStatus =>
        JobService.GetJob(ScanJobKey) is { State: BackgroundJobState.Running } job
            ? job.DisplayMessage
            : null;

    /// <summary>
    /// Starts a background job under PageJobKey - the globally-mounted BackgroundJobOverlay (keyed by the
    /// same URL path) picks it up automatically and renders LoadingOverlay with the job's terminal, the
    /// same pattern Workspace Repositories uses for push/update. Reserved for operations that touch many
    /// files or repositories (commit, whole-repository/section/multi-repository stage-unstage, manual
    /// refresh) - single-file/folder stage/unstage stay on the lightweight inline indicator instead.
    /// </summary>
    private void StartPageJob(
        string label,
        Func<BackgroundJobHandle, CancellationToken, Task> work,
        PageJobOptions? options = null)
    {
        options ??= new PageJobOptions();
        JobService.StartJob(PageJobKey, label, async (job, ct) =>
        {
            try
            {
                await work(job, ct);
                if (options.ReloadOnSuccess)
                {
                    await InvokeAsync(async () =>
                    {
                        if (_disposed)
                        {
                            return;
                        }

                        await LoadAsync();
                    });
                }
            }
            catch (OperationCanceledException)
            {
                if (options.CancelToast != null)
                {
                    SafeInvoke(() => ToastService.Show(options.CancelToast));
                }

                throw;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Git Changes background job failed");
                options.OnError?.Invoke(ex);
                throw;
            }
        });
    }

    /// <summary>
    /// Non-overlay workspace scan under ScanJobKey - used by the empty-state Refresh and the
    /// manual Refresh button (on-open warm-up is started by <see cref="IWorkspaceGitChangesActivation"/>).
    /// Survives page navigation (circuit-scoped BackgroundJobService); the empty-state UI
    /// and the header's scan indicator both bind to IsScanRunning / ScanStatus when the page is
    /// mounted, so the panel is never fully hidden behind a rescan.
    /// Context id is captured when the job body starts so a later Feature switch cannot retarget
    /// an in-flight scan (the switch aborts the scan separately).
    /// </summary>
    private void StartScanJob(string label)
    {
        JobService.StartJob(ScanJobKey, label, async (job, ct) =>
        {
            try
            {
                // Capture once - do not re-read _selectedContextId mid-scan.
                var contextId = RequireSelectedContextId();
                await Scanner.ScanWorkspaceAsync(WorkspaceId, contextId, ct, progress =>
                    job.ReportProgress($"Refreshing {progress.Completed} of {progress.Total} repositories..."), includeLineStats: true);

                await InvokeAsync(async () =>
                {
                    if (_disposed)
                    {
                        return;
                    }

                    await LoadAsync();
                });
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Git Changes refresh failed for workspace {WorkspaceId}", WorkspaceId);
                SafeInvoke(() => ToastService.ShowError("Refresh failed. See logs for details."));
                throw;
            }
        });
    }

    private void AbortScan() => JobService.GetJob(ScanJobKey)?.Abort();

    private void SafeInvoke(Action callback)
    {
        if (_disposed)
        {
            return;
        }

        _ = InvokeAsync(() =>
        {
            if (!_disposed)
            {
                callback();
                StateHasChanged();
            }
        });
    }
}
