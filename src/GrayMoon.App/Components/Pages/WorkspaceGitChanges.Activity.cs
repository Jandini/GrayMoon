using GrayMoon.App.Services.GitChanges;
using Microsoft.AspNetCore.Components;

namespace GrayMoon.App.Components.Pages;

public sealed partial class WorkspaceGitChanges
{
    [Inject] private IWorkspaceGitChangesActivation GitChangesActivation { get; set; } = default!;
    [Inject] private IGitChangesWorkspaceScanner Scanner { get; set; } = default!;

    private IDisposable? _activityLease;
    private int? _activityLeaseWorkspaceId;

    /// <summary>
    /// Leases workspace activity for as long as this page is open. A cold start kicks off the shared
    /// warm-up scan (same job Repositories uses); that job survives navigation, the header/empty-state
    /// scan indicator binds to it, and updates keep arriving via GitChangesUpdated -> LoadAsync.
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
        _activityLease = GitChangesActivation.Activate(WorkspaceId);
    }

    private void ReleaseActivitySubscription()
    {
        _activityLease?.Dispose();
        _activityLease = null;
        _activityLeaseWorkspaceId = null;
    }
}
