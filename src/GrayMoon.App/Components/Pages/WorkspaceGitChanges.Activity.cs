using GrayMoon.App.Services.GitChanges;
using Microsoft.AspNetCore.Components;

namespace GrayMoon.App.Components.Pages;

public sealed partial class WorkspaceGitChanges
{
    [Inject] private IGitChangesWorkspaceScanner Scanner { get; set; } = default!;
    [Inject] private IGitChangesLineStatsRefresh LineStatsRefresh { get; set; } = default!;

    private IDisposable? _lineStatsLease;
    private int? _lineStatsLeaseWorkspaceId;

    /// <summary>
    /// Subscribes to silent +/- fill-in while Changes is open. Workspace activity / watcher renewal is
    /// owned by the layout <c>WorkspaceGitChangesActivityBinder</c> for any workspace route.
    /// </summary>
    private void EnsureActivitySubscription()
    {
        if (_lineStatsLeaseWorkspaceId == WorkspaceId)
        {
            return;
        }

        _lineStatsLease?.Dispose();
        _lineStatsLease = null;
        _lineStatsLeaseWorkspaceId = WorkspaceId;
        _lineStatsLease = LineStatsRefresh.Subscribe(WorkspaceId);

        // Cold-start warm-up (layout binder) already includes line stats when the workspace was cold.
        // When already active (no scan job), fill +/- in the background and show them when the snapshot lands.
        if (!IsAnyScanRunning)
        {
            LineStatsRefresh.RequestWorkspace(WorkspaceId);
        }
    }

    private void ReleaseActivitySubscription()
    {
        _lineStatsLease?.Dispose();
        _lineStatsLease = null;
        _lineStatsLeaseWorkspaceId = null;
    }
}
