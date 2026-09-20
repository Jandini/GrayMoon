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

    private bool IsJobRunning => JobService.IsRunning(PageJobKey);
    private bool IsScanRunning => JobService.IsRunning(ScanJobKey);
    private bool IsAnyScanRunning => IsJobRunning || IsScanRunning;

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
    /// </summary>
    private void StartScanJob(string label)
    {
        JobService.StartJob(ScanJobKey, label, async (job, ct) =>
        {
            try
            {
                await Scanner.ScanWorkspaceAsync(WorkspaceId, RequireSelectedContextId(), ct, progress =>
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

