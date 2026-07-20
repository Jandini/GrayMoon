using GrayMoon.App.Services.GitChanges;
using Microsoft.AspNetCore.Components;

namespace GrayMoon.App.Components.Pages;

public sealed partial class WorkspaceGitChanges
{
    [Inject] private IWorkspaceGitChangesActivityTracker ActivityTracker { get; set; } = default!;
    [Inject] private IGitChangesWorkspaceScanner Scanner { get; set; } = default!;

    private IDisposable? _activityLease;
    private int? _activityLeaseWorkspaceId;

    /// <summary>
    /// Leases workspace activity for as long as this page is open (mirrors the Agent's watcher-lease
    /// pattern, just App-side and keyed by workspace). If the workspace was not already active - meaning
    /// background monitoring has not been sweeping it and its Agent-side watchers may be idle or never
    /// started - kicks off a one-time warm-up scan via EmptyScanJobKey instead of waiting for the next
    /// periodic sweep. That job survives navigation; when the page is empty the inline spinner binds to
    /// it, and when the tree is showing updates arrive via GitChangesUpdated -> LoadAsync.
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

        if (!wasActive && AgentBridge.IsAgentConnected && !IsAnyScanRunning)
        {
            StartEmptyScanJob("Refreshing repositories...");
        }
    }

    private void ReleaseActivitySubscription()
    {
        _activityLease?.Dispose();
        _activityLease = null;
        _activityLeaseWorkspaceId = null;
    }
}
