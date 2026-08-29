namespace GrayMoon.App.Components.Pages;

/// <summary>
/// Keeps PR badges fresh automatically instead of relying on hover. Every tick refreshes only the
/// currently visible (virtualized) rows via WorkspacePullRequestService, which now calls GitHub through
/// GetETaggedAsync - most polls come back as a free 304 rather than costing primary rate-limit quota.
/// Action-triggered instant refreshes (push, sync-to-default, create PR) are unaffected and still fire
/// immediately with force: true.
/// </summary>
public sealed partial class WorkspaceRepositories
{
    private const int PrPollIntervalMs = 15_000;
    private CancellationTokenSource? _prPollCts;

    /// <summary>Starts the background PR polling loop for the lifetime of this component instance.</summary>
    private void StartPrPollingLoop()
    {
        _prPollCts = new CancellationTokenSource();
        _ = RunPrPollingLoopAsync(_prPollCts.Token);
    }

    private async Task RunPrPollingLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && !_disposed)
        {
            try
            {
                await Task.Delay(PrPollIntervalMs, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
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
        _prPollCts?.Cancel();
        _prPollCts?.Dispose();
        _prPollCts = null;
    }
}
