namespace GrayMoon.Application.Features;

/// <summary>
/// Hierarchical lock: ordinary mutations lock per <see cref="WorkspaceFeatureContextId"/>;
/// structural Workspace mutations lock the whole Workspace and block all contexts.
/// </summary>
public interface IWorkspaceOperationLock
{
    bool IsWorkspaceStructurallyBusy(int workspaceId);

    bool IsContextBusy(WorkspaceFeatureContextId contextId);

    bool TryStartContext(
        WorkspaceFeatureContextId contextId,
        int workspaceId,
        string operationKind,
        string overlayKey,
        string displayMessage,
        Func<IWorkspaceLockedOperation, CancellationToken, Task> work,
        out IWorkspaceLockedOperation operation);

    bool TryStartStructural(
        int workspaceId,
        string operationKind,
        string overlayKey,
        string displayMessage,
        Func<IWorkspaceLockedOperation, CancellationToken, Task> work,
        out IWorkspaceLockedOperation operation);
}

public interface IWorkspaceLockedOperation
{
    int WorkspaceId { get; }
    WorkspaceFeatureContextId? ContextId { get; }
    string OperationKind { get; }
    string OverlayKey { get; }
    string DisplayMessage { get; }

    /// <summary>Completes only after the lock has been released, so a caller can hand control back with the lock free.</summary>
    Task WhenCompleted { get; }
    CancellationToken CancellationToken { get; }
    void ReportProgress(string message);

    /// <summary>
    /// Runs <paramref name="work"/> as a phase the user may send to the background: while it runs the overlay offers
    /// Background instead of Abort. Background cancels only the token passed to <paramref name="work"/>, and this
    /// returns normally, so the operation completes and its overlay closes. <paramref name="work"/> must therefore
    /// only watch something that keeps going on its own (such as a Worker build).
    /// </summary>
    Task RunBackgroundableAsync(Func<CancellationToken, Task> work);
}
