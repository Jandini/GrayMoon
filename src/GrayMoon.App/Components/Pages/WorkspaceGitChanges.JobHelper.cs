using GrayMoon.App.Services;

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
    private bool IsJobRunning => JobService.IsRunning(PageJobKey);

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
