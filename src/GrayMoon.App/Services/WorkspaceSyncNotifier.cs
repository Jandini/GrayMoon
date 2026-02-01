using System.Collections.Concurrent;

namespace GrayMoon.App.Services;

public sealed class WorkspaceSyncNotifier : IWorkspaceSyncNotifier
{
    private readonly ConcurrentDictionary<int, ConcurrentBag<TaskCompletionSource>> _waiters = new();

    public void NotifySyncCompleted(int workspaceId)
    {
        if (!_waiters.TryRemove(workspaceId, out var bag))
            return;
        foreach (var tcs in bag)
            tcs.TrySetResult();
    }

    public Task WaitForSyncAsync(int workspaceId, CancellationToken cancellationToken = default)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bag = _waiters.GetOrAdd(workspaceId, _ => new ConcurrentBag<TaskCompletionSource>());
        bag.Add(tcs);
        if (cancellationToken.CanBeCanceled)
            cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));
        return tcs.Task;
    }
}
