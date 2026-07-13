using GrayMoon.App.Services.GitChanges;
using Microsoft.AspNetCore.Components;

namespace GrayMoon.App.Components.Pages;

public sealed partial class WorkspaceGitChanges
{
    [Inject] private IWorkspaceGitChangesActivityTracker ActivityTracker { get; set; } = default!;
    [Inject] private IGitChangesWorkspaceScanner Scanner { get; set; } = default!;

    private IDisposable? _activityLease;
    private int? _activityLeaseWorkspaceId;
    private bool _isWarmingUp;
    private string? _warmUpStatus;

    /// <summary>
    /// Leases workspace activity for as long as this page is open (mirrors the Agent's watcher-lease
    /// pattern, just App-side and keyed by workspace). If the workspace was not already active - meaning
    /// background monitoring has not been sweeping it and its Agent-side watchers may be idle or never
    /// started - kicks off a one-time warm-up scan instead of waiting for the next periodic sweep.
    /// </summary>
    private void EnsureActivitySubscription()
    {
        if (_activityLeaseWorkspaceId == WorkspaceId)
        {
            return;
        }

        _activityLease?.Dispose();
        _activityLease = null;
        _activityLeaseWorkspaceId = WorkspaceId;

        var wasActive = ActivityTracker.IsActive(WorkspaceId);
        _activityLease = ActivityTracker.Subscribe(WorkspaceId);

        if (!wasActive)
        {
            _isWarmingUp = true;
            _warmUpStatus = "Refreshing repositories...";
            _ = RunWarmUpScanAsync(WorkspaceId);
        }
    }

    // Silent-but-visible: no LoadingOverlay for this one - it runs automatically, not from a user click,
    // so it only surfaces as a small header status text. Tree updates arrive incrementally the same way
    // periodic sweep results already do, via GitChangesUpdated -> LoadAsync.
    private async Task RunWarmUpScanAsync(int workspaceId)
    {
        try
        {
            await Scanner.ScanWorkspaceAsync(workspaceId, CancellationToken.None, progress =>
                SafeInvoke(() => _warmUpStatus = $"Refreshing {progress.Completed} of {progress.Total} repositories..."));
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Git Changes warm-up scan failed for workspace {WorkspaceId}", workspaceId);
        }
        finally
        {
            SafeInvoke(() =>
            {
                _isWarmingUp = false;
                _warmUpStatus = null;
            });
        }
    }

    private void ReleaseActivitySubscription()
    {
        _activityLease?.Dispose();
        _activityLease = null;
        _activityLeaseWorkspaceId = null;
    }
}
