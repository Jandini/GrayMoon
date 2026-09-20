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
    CancellationToken CancellationToken { get; }
    void ReportProgress(string message);
}
