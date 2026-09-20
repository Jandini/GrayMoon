using System.Collections.Concurrent;
using GrayMoon.Application.Features;

namespace GrayMoon.App.Services.Jobs;

/// <summary>
/// Hierarchical process-wide lock: structural Workspace mutations block all contexts;
/// ordinary mutations lock per <see cref="WorkspaceFeatureContextId"/> and may run concurrently.
/// </summary>
public sealed class WorkspaceOperationRunner(ILogger<WorkspaceOperationRunner> logger)
    : IWorkspaceOperationRunner, IWorkspaceOperationLock
{
    private readonly object _gate = new();
    private readonly ConcurrentDictionary<int, WorkspaceOperation> _structuralByWorkspace = new();
    private readonly ConcurrentDictionary<int, WorkspaceOperation> _contextByContextId = new();
    private readonly ConcurrentDictionary<int, ConcurrentDictionary<int, byte>> _contextIdsByWorkspace = new();

    public event Action? Changed;

    public WorkspaceOperation? GetRunning(int workspaceId)
    {
        if (_structuralByWorkspace.TryGetValue(workspaceId, out var structural))
            return structural;

        if (_contextIdsByWorkspace.TryGetValue(workspaceId, out var set))
        {
            foreach (var contextId in set.Keys)
            {
                if (_contextByContextId.TryGetValue(contextId, out var op))
                    return op;
            }
        }

        return null;
    }

    public WorkspaceOperation? GetRunningForContext(WorkspaceFeatureContextId contextId)
        => _contextByContextId.TryGetValue(contextId.Value, out var op) ? op : null;

    public bool IsBusy(int workspaceId)
        => IsWorkspaceStructurallyBusy(workspaceId) || HasAnyContextMutation(workspaceId);

    public bool IsWorkspaceStructurallyBusy(int workspaceId)
        => _structuralByWorkspace.ContainsKey(workspaceId);

    public bool IsContextBusy(WorkspaceFeatureContextId contextId)
        => _contextByContextId.ContainsKey(contextId.Value);

    /// <summary>
    /// Legacy entry: treated as a Workspace-structural lock so existing callers remain safe
    /// until they migrate to context-scoped keys.
    /// </summary>
    public bool TryStart(
        int workspaceId,
        string operationKind,
        string overlayKey,
        string displayMessage,
        Func<WorkspaceOperation, CancellationToken, Task> work,
        out WorkspaceOperation operation)
        => TryStartStructuralCore(workspaceId, operationKind, overlayKey, displayMessage, work, out operation);

    public bool TryStartStructural(
        int workspaceId,
        string operationKind,
        string overlayKey,
        string displayMessage,
        Func<IWorkspaceLockedOperation, CancellationToken, Task> work,
        out IWorkspaceLockedOperation operation)
    {
        var started = TryStartStructuralCore(
            workspaceId,
            operationKind,
            overlayKey,
            displayMessage,
            async (op, ct) => await work(op, ct),
            out var concrete);
        operation = concrete;
        return started;
    }

    public bool TryStartContext(
        WorkspaceFeatureContextId contextId,
        int workspaceId,
        string operationKind,
        string overlayKey,
        string displayMessage,
        Func<IWorkspaceLockedOperation, CancellationToken, Task> work,
        out IWorkspaceLockedOperation operation)
    {
        lock (_gate)
        {
            if (IsWorkspaceStructurallyBusy(workspaceId))
            {
                operation = _structuralByWorkspace[workspaceId];
                return false;
            }

            if (_contextByContextId.TryGetValue(contextId.Value, out var existing))
            {
                operation = existing;
                return false;
            }

            var created = new WorkspaceOperation(
                workspaceId, operationKind, overlayKey, displayMessage, contextId, isStructural: false);
            if (!_contextByContextId.TryAdd(contextId.Value, created))
            {
                operation = _contextByContextId[contextId.Value];
                created.Dispose();
                return false;
            }

            var set = _contextIdsByWorkspace.GetOrAdd(workspaceId, _ => new ConcurrentDictionary<int, byte>());
            set[contextId.Value] = 0;
            operation = created;
            created.Changed += RaiseChanged;
            _ = RunAsync(created, work, onFinally: () =>
            {
                created.Changed -= RaiseChanged;
                _contextByContextId.TryRemove(contextId.Value, out _);
                if (_contextIdsByWorkspace.TryGetValue(workspaceId, out var wsSet))
                {
                    wsSet.TryRemove(contextId.Value, out _);
                    if (wsSet.IsEmpty)
                        _contextIdsByWorkspace.TryRemove(workspaceId, out _);
                }
            });
        }

        RaiseChanged();
        return true;
    }

    private bool TryStartStructuralCore(
        int workspaceId,
        string operationKind,
        string overlayKey,
        string displayMessage,
        Func<WorkspaceOperation, CancellationToken, Task> work,
        out WorkspaceOperation operation)
    {
        lock (_gate)
        {
            if (_structuralByWorkspace.TryGetValue(workspaceId, out var existingStructural))
            {
                operation = existingStructural;
                return false;
            }

            if (HasAnyContextMutation(workspaceId))
            {
                operation = GetRunning(workspaceId)!;
                return false;
            }

            var created = new WorkspaceOperation(
                workspaceId, operationKind, overlayKey, displayMessage, contextId: null, isStructural: true);
            if (!_structuralByWorkspace.TryAdd(workspaceId, created))
            {
                operation = _structuralByWorkspace[workspaceId];
                created.Dispose();
                return false;
            }

            operation = created;
            created.Changed += RaiseChanged;
            _ = RunAsync(created, async (op, ct) => await work((WorkspaceOperation)op, ct), onFinally: () =>
            {
                created.Changed -= RaiseChanged;
                _structuralByWorkspace.TryRemove(new KeyValuePair<int, WorkspaceOperation>(workspaceId, created));
            });
        }

        RaiseChanged();
        return true;
    }

    private Task RunAsync(
        WorkspaceOperation created,
        Func<IWorkspaceLockedOperation, CancellationToken, Task> work,
        Action onFinally)
        => Task.Run(async () =>
        {
            using var _ = TerminalSinkContext.Use(created.Terminal);
            try
            {
                await work(created, created.CancellationToken);
                created.MarkCompleted();
            }
            catch (OperationCanceledException)
            {
                created.MarkAborted();
            }
            catch (Exception ex)
            {
                logger.LogError(
                    ex,
                    "Workspace {WorkspaceId} operation {OperationKind} failed: {Message}",
                    created.WorkspaceId,
                    created.OperationKind,
                    ex.Message);
                created.MarkFaulted(ex);
            }
            finally
            {
                onFinally();
                created.NotifySettled();
                RaiseChanged();
            }
        });

    private bool HasAnyContextMutation(int workspaceId)
        => _contextIdsByWorkspace.TryGetValue(workspaceId, out var set) && !set.IsEmpty;

    private void RaiseChanged() => Changed?.Invoke();
}
