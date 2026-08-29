using GrayMoon.App.Services;

namespace GrayMoon.App.Components.Pages;

/// <summary>
/// Keeps PR badges fresh automatically instead of relying on hover. Every tick refreshes only the
/// currently visible (virtualized) rows via WorkspacePullRequestService, which now calls GitHub through
/// GetETaggedAsync - most polls come back as a free 304 rather than costing primary rate-limit quota.
/// The interval scales with AppActivityStateService (Active/Idle/Hidden) so an actively-watched grid polls
/// fast while a backgrounded or unattended tab backs off - and a poll fires immediately the moment the
/// user becomes active again, rather than waiting out whatever slower delay was in flight.
/// Action-triggered instant refreshes (push, sync-to-default, create PR) are unaffected and still fire
/// immediately with force: true.
/// </summary>
public sealed partial class WorkspaceRepositories
{
    private const int PrPollIntervalActiveMs = 2_000;
    private const int PrPollIntervalIdleMs = 5_000;
    private const int PrPollIntervalHiddenMs = 30_000;
    private CancellationTokenSource? _prPollCts;
    private CancellationTokenSource? _prPollWakeCts;

    /// <summary>Starts the background PR polling loop for the lifetime of this component instance.</summary>
    private void StartPrPollingLoop()
    {
        _prPollCts = new CancellationTokenSource();
        ActivityStateService.BecameActive += OnActivityBecameActive;
        _ = RunPrPollingLoopAsync(_prPollCts.Token);
    }

    private void OnActivityBecameActive()
    {
        // Interrupt whatever delay is currently in flight (Idle's 5s or Hidden's 30s) so resuming
        // activity always triggers an immediate poll instead of waiting the rest of it out.
        _prPollWakeCts?.Cancel();
    }

    private static int CurrentPollDelayMs(AppActivityState state) => state switch
    {
        AppActivityState.Active => PrPollIntervalActiveMs,
        AppActivityState.Idle => PrPollIntervalIdleMs,
        AppActivityState.Hidden => PrPollIntervalHiddenMs,
        _ => PrPollIntervalIdleMs,
    };

    private async Task RunPrPollingLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && !_disposed)
        {
            var delayMs = CurrentPollDelayMs(ActivityStateService.State);
            _prPollWakeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            try
            {
                await Task.Delay(delayMs, _prPollWakeCts.Token);
            }
            catch (OperationCanceledException)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                // Otherwise this was a wake-up from OnActivityBecameActive - fall through and poll now.
            }
            finally
            {
                _prPollWakeCts.Dispose();
                _prPollWakeCts = null;
            }

            if (_disposed || cancellationToken.IsCancellationRequested)
            {
                return;
            }

            try
            {
                var repositoryIds = GetVisibleRepositoryIds();
                if (repositoryIds.Count == 0)
                {
                    continue;
                }

                await WorkspacePageService.WorkspacePullRequestService.RefreshPullRequestsAsync(
                    WorkspaceId, repositoryIds, cancellationToken: cancellationToken);
                if (_disposed || cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                await RefreshVisibleRowsAsync(cancellationToken);
                await InvokeAsync(StateHasChanged);
                await RefreshOpenMergeDialogIfDueAsync();
            }
            catch (OperationCanceledException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
            catch (InvalidOperationException)
            {
            }
            catch (Exception ex)
            {
                Logger.LogDebug(ex, "Background PR poll failed for workspace {WorkspaceId}", WorkspaceId);
            }
        }
    }

    /// <summary>Distinct repository ids for slots currently in the virtualized viewport.</summary>
    private List<int> GetVisibleRepositoryIds() =>
        VisibleSlots.Where(s => s.Kind == VirtualSlotKind.Row).Select(s => s.RepositoryId).Distinct().ToList();

    private void StopPrPollingLoop()
    {
        ActivityStateService.BecameActive -= OnActivityBecameActive;
        _prPollWakeCts?.Cancel();
        _prPollCts?.Cancel();
        _prPollCts?.Dispose();
        _prPollCts = null;
    }
}
