using GrayMoon.App.Services.GitChanges;
using Microsoft.AspNetCore.Components;

namespace GrayMoon.App.Components.Pages;

public sealed partial class WorkspaceRepositories
{
    [Inject] private IWorkspaceGitChangesActivation GitChangesActivation { get; set; } = default!;

    private IDisposable? _gitChangesActivityLease;
    private int? _gitChangesActivityLeaseWorkspaceId;

    /// <summary>
    /// Same activity lease + cold-start warm-up as Changes. Idempotent while this page already
    /// holds a lease for <see cref="WorkspaceId"/>; a workspace that is already active is left alone.
    /// </summary>
    private void EnsureGitChangesActivation()
    {
        if (_gitChangesActivityLeaseWorkspaceId == WorkspaceId)
        {
            return;
        }

        _gitChangesActivityLease?.Dispose();
        _gitChangesActivityLease = null;
        _gitChangesActivityLeaseWorkspaceId = WorkspaceId;
        _gitChangesActivityLease = GitChangesActivation.Activate(WorkspaceId);
    }

    private void ReleaseGitChangesActivation()
    {
        _gitChangesActivityLease?.Dispose();
        _gitChangesActivityLease = null;
        _gitChangesActivityLeaseWorkspaceId = null;
    }
}
