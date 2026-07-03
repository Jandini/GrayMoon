using GrayMoon.App.Services;

namespace GrayMoon.App.Components.Pages;

public sealed partial class WorkspaceRepositories
{
    private sealed record PageJobOptions
    {
        /// <summary>When true (default), calls RefreshFromSync inside InvokeAsync after work completes without exception.</summary>
        public bool RefreshOnSuccess { get; init; } = true;
        /// <summary>When true, calls ReloadWorkspaceDataAfterCancelAsync on OperationCanceledException before re-throw.</summary>
        public bool RefreshOnCancel { get; init; } = false;
        /// <summary>Toast message shown via ToastService.Show on OperationCanceledException. Null means no toast.</summary>
        public string? CancelToast { get; init; }
        /// <summary>If set, Logger.LogError is called with this message on a general Exception catch.</summary>
        public string? FailureLogMessage { get; init; }
        /// <summary>Called with the exception on a general Exception catch (after optional FailureLogMessage logging). Null means no callback.</summary>
        public Action<Exception>? OnError { get; init; }
    }

    /// <summary>
    /// Starts a background job under PageJobKey, wrapping the work delegate in a standard
    /// try/catch skeleton that handles OperationCanceledException and general Exception
    /// according to the supplied options.
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
                if (options.RefreshOnSuccess)
                    await InvokeAsync(async () =>
                    {
                        if (_disposed) return;
                        await RefreshFromSync();
                    });
            }
            catch (OperationCanceledException)
            {
                if (options.RefreshOnCancel)
                    await ReloadWorkspaceDataAfterCancelAsync();
                if (options.CancelToast != null)
                    SafeInvoke(() => ToastService.Show(options.CancelToast));
                throw;
            }
            catch (Exception ex)
            {
                if (options.FailureLogMessage != null)
                    Logger.LogError(ex, options.FailureLogMessage);
                options.OnError?.Invoke(ex);
                throw;
            }
        });
    }
}
