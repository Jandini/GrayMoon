namespace GrayMoon.App.Services.GitChanges;

/// <summary>
/// Holds at most one Git Changes activity lease for the workspace currently in view on this circuit.
/// Same-workspace page navigations keep the lease; switching workspaces or leaving workspace routes
/// releases into <see cref="IWorkspaceGitChangesActivityTracker"/>'s grace window.
/// </summary>
public sealed class WorkspaceGitChangesRouteActivity(IWorkspaceGitChangesActivation activation) : IDisposable
{
    private IDisposable? _lease;
    private int? _workspaceId;
    private bool _disposed;

    /// <summary>Workspace currently leased by this circuit, if any.</summary>
    public int? CurrentWorkspaceId => _workspaceId;

    /// <summary>
    /// Aligns the held lease with <paramref name="workspaceId"/>. No-op when the id is unchanged.
    /// Passing null releases the lease (navigate out of workspace routes).
    /// </summary>
    public void Sync(int? workspaceId)
    {
        // Circuit teardown disposes this scoped service before the layout component. The binder's
        // Dispose still calls Sync(null) to drop the lease; Dispose has already done that.
        if (_disposed)
        {
            return;
        }

        if (_workspaceId == workspaceId)
        {
            return;
        }

        _lease?.Dispose();
        _lease = null;
        _workspaceId = workspaceId;

        if (workspaceId is int id)
        {
            _lease = activation.Activate(id);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _lease?.Dispose();
        _lease = null;
        _workspaceId = null;
    }
}
