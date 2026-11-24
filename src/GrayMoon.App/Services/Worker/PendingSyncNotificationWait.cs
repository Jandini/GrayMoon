namespace GrayMoon.App.Services.Worker;

/// <summary>
/// Waits on <see cref="IWorkerSyncNotificationBarrier"/> and, while it waits, reports how many repository updates
/// of the Workspace are still to be applied, so the overlay counts down instead of looking frozen.
/// </summary>
internal static class PendingSyncNotificationWait
{
    internal const string PlainMessage = "Applying pending repository changes...";

    internal static string Message(int pending) =>
        pending > 0 ? $"Applying {pending} pending repository change{(pending == 1 ? "" : "s")}..." : PlainMessage;

    public static async Task WaitAsync(
        IWorkerSyncNotificationBarrier barrier,
        AppBackgroundWorkStateService state,
        int workspaceId,
        Action<string> setProgress,
        CancellationToken cancellationToken)
    {
        void Report(object? sender, EventArgs e)
        {
            // Zero means the wait is about to end; the caller's next message replaces this one.
            var pending = state.GetPendingCountForWorkspace(workspaceId);
            if (pending > 0)
                setProgress(Message(pending));
        }

        setProgress(Message(state.GetPendingCountForWorkspace(workspaceId)));
        state.OnStateChanged(Report);
        try
        {
            await barrier.WaitForPendingAsync(workspaceId, cancellationToken);
        }
        finally
        {
            state.RemoveStateChanged(Report);
        }
    }
}
