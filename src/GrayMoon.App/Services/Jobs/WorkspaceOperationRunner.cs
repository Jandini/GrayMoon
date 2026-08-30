using System.Collections.Concurrent;

namespace GrayMoon.App.Services.Jobs;

/// <summary>
/// Singleton host for in-flight workspace mutations. Work continues after a circuit
/// disposes so another tab (or later REST/MCP) can attach to the same run.
/// </summary>
public sealed class WorkspaceOperationRunner : IWorkspaceOperationRunner
{
    private readonly ConcurrentDictionary<int, WorkspaceOperation> _running = new();

    public event Action? Changed;

    public WorkspaceOperation? GetRunning(int workspaceId)
        => _running.TryGetValue(workspaceId, out var op) ? op : null;

    public bool IsBusy(int workspaceId)
        => _running.ContainsKey(workspaceId);

    public bool TryStart(
        int workspaceId,
        string operationKind,
        string overlayFamily,
        string displayMessage,
        Func<WorkspaceOperation, CancellationToken, Task> work,
        out WorkspaceOperation operation)
    {
        var created = new WorkspaceOperation(workspaceId, operationKind, overlayFamily, displayMessage);
        if (!_running.TryAdd(workspaceId, created))
        {
            operation = _running[workspaceId];
            created.Dispose();
            return false;
        }

        operation = created;
        created.Changed += RaiseChanged;
        _ = Task.Run(async () =>
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
                created.MarkFaulted(ex);
            }
            finally
            {
                created.Changed -= RaiseChanged;
                _running.TryRemove(new KeyValuePair<int, WorkspaceOperation>(workspaceId, created));
                RaiseChanged();
            }
        });

        RaiseChanged();
        return true;
    }

    private void RaiseChanged()
    {
        var handler = Changed;
        handler?.Invoke();
    }
}
