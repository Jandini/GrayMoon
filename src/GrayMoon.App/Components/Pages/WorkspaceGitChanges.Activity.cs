using GrayMoon.App.Services.GitChanges;
using Microsoft.AspNetCore.Components;

namespace GrayMoon.App.Components.Pages;

public sealed partial class WorkspaceGitChanges
{
    [Inject] private IWorkspaceGitChangesActivation GitChangesActivation { get; set; } = default!;
    [Inject] private IGitChangesWorkspaceScanner Scanner { get; set; } = default!;
    [Inject] private IGitChangesLineStatsRefresh LineStatsRefresh { get; set; } = default!;

    private IDisposable? _activityLease;
    private IDisposable? _lineStatsLease;
    private int? _activityLeaseWorkspaceId;

    /// <summary>
    /// Leases workspace activity for as long as this page is open. A cold start kicks off the shared
    /// warm-up scan (same job Repositories uses); that job survives navigation, the header/empty-state
    /// scan indicator binds to it, and updates keep arriving via GitChangesUpdated -> LoadAsync.
    /// Also subscribes to silent +/- fill-in so the header can populate without a manual Refresh.
    /// </summary>
    private void EnsureActivitySubscription()
    {
        if (_activityLeaseWorkspaceId == WorkspaceId)
        {
            return;
        }

        _activityLease?.Dispose();
        _lineStatsLease?.Dispose();
        _activityLease = null;
        _lineStatsLease = null;
        _activityLeaseWorkspaceId = WorkspaceId;
        _activityLease = GitChangesActivation.Activate(WorkspaceId);
        _lineStatsLease = LineStatsRefresh.Subscribe(WorkspaceId);

        // Cold-start warm-up already includes line stats. When the workspace is already active
        // (no scan job), fill +/- in the background and show them when the snapshot lands.
        if (!IsAnyScanRunning)
        {
            LineStatsRefresh.RequestWorkspace(WorkspaceId);
        }
    }

    private void ReleaseActivitySubscription()
    {
        _activityLease?.Dispose();
        _lineStatsLease?.Dispose();
        _activityLease = null;
        _lineStatsLease = null;
        _activityLeaseWorkspaceId = null;
    }
}
