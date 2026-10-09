namespace GrayMoon.App.Services.Workspaces;

/// <summary>
/// Lets many hook syncs for one Feature context share the workspace-wide recompute that follows each of their state
/// writes. Create Feature releases a sync per repository at about the same time, and each used to run the whole
/// file-version check and dependency-stat recompute by itself, one after another.
/// </summary>
/// <remarks>
/// At most one run per key executes at a time. A caller that arrives while a run is in progress waits for the next run,
/// which starts after that caller's own state write, and callers that arrive meanwhile share that single next run.
/// So when <see cref="RunAsync"/> returns, a run that started after the caller's write has finished: nothing a caller
/// wrote is left out of the recompute, a burst of N syncs just costs two runs instead of N.
/// </remarks>
public sealed class SyncRecomputeCoalescer
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Slot> _slots = new(StringComparer.Ordinal);

    private sealed class Slot
    {
        public TaskCompletionSource? Current { get; set; }
        public TaskCompletionSource? Pending { get; set; }
    }

    public async Task RunAsync(string key, Func<Task> work)
    {
        TaskCompletionSource batch;
        Task? waitFor = null;
        bool runsBatch;

        lock (_gate)
        {
            if (!_slots.TryGetValue(key, out var slot))
                _slots[key] = slot = new Slot();

            if (slot.Current is null)
            {
                batch = slot.Current = NewCompletion();
                runsBatch = true;
            }
            else if (slot.Pending is null)
            {
                // First caller to arrive during a run: waits for it, then runs the next batch for everyone queued behind it.
                batch = slot.Pending = NewCompletion();
                waitFor = slot.Current.Task;
                runsBatch = true;
            }
            else
            {
                batch = slot.Pending;
                runsBatch = false;
            }
        }

        if (!runsBatch)
        {
            await batch.Task;
            return;
        }

        if (waitFor is not null)
        {
            try { await waitFor; }
            catch { /* the previous run's failure belongs to its own callers */ }
        }

        try
        {
            await work();
            Finish(key, batch, null);
        }
        catch (Exception ex)
        {
            Finish(key, batch, ex);
            throw;
        }
    }

    private void Finish(string key, TaskCompletionSource batch, Exception? error)
    {
        lock (_gate)
        {
            var slot = _slots[key];
            if (slot.Pending is not null)
            {
                // The waiting batch's runner wakes when this batch completes below and is already the current run.
                slot.Current = slot.Pending;
                slot.Pending = null;
            }
            else
            {
                slot.Current = null;
                _slots.Remove(key);
            }
        }

        if (error is null)
            batch.TrySetResult();
        else
            batch.TrySetException(error);
    }

    private static TaskCompletionSource NewCompletion() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
