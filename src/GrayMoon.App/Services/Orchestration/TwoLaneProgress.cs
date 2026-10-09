namespace GrayMoon.App.Services.Orchestration;

/// <summary>
/// Merges the progress of the update lane and the push lane into the overlay's single message. The overlay keeps its
/// one-operation layout: whichever lane reported last is what is shown, and the overlay transitions to it. When a lane
/// ends, the other lane's latest message is shown again. Both lanes report from thread-pool threads, so every change
/// is serialized and published in order.
/// </summary>
public sealed class TwoLaneProgress(Action<string> report)
{
    private readonly object _gate = new();
    private string? _update;
    private string? _push;

    public Action<string> Update => message => { lock (_gate) { _update = message; Show(message); } };

    public Action<string> Push => message => { lock (_gate) { _push = message; Show(message); } };

    /// <summary>Drops the update lane once it has finished and shows the push lane's latest message.</summary>
    public void EndUpdate() { lock (_gate) { _update = null; Show(_push); } }

    /// <summary>Drops the push lane once it has finished and shows the update lane's latest message.</summary>
    public void EndPush() { lock (_gate) { _push = null; Show(_update); } }

    private void Show(string? message)
    {
        if (!string.IsNullOrWhiteSpace(message))
            report(message.Trim());
    }
}

/// <summary>
/// <see cref="IProgress{T}"/> that runs the callback on the reporting thread. <see cref="Progress{T}"/> posts to the
/// thread pool when there is no synchronization context, which can reorder messages; the lanes need them in order.
/// </summary>
public sealed class SynchronousProgress(Action<string> report) : IProgress<OperationProgress>
{
    public void Report(OperationProgress value) => report(value.Message);
}
