namespace GrayMoon.App.Services.Features;

/// <summary>
/// Marks Features interrupted mid-create/remove as NeedsRepair and compares Feature worktree
/// rows to the Worker's git worktree list (C2).
/// </summary>
public interface IWorkspaceFeatureReconciler
{
    Task ReconcileAsync(CancellationToken cancellationToken = default);
}
