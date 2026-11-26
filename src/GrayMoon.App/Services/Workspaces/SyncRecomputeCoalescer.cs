namespace GrayMoon.App.Services.Workspaces;

/// <summary>
/// Keeps the context-wide recomputes of one Feature context from overlapping. The hook sync queue has one reader, so
/// its own recomputes never meet each other (it runs one per batch, see <c>WorkerSyncNotificationQueue</c>); the callers
/// that do meet are a hook batch and a pull's refresh of the same context (<c>WorkspaceCommitSyncHandler</c>).
/// <see cref="WorkspaceFileVersionService"/> gates its file-version check with an instance of its own.
/// </summary>
/// <remarks>
/// At most one run per key executes at a time. A caller that arrives while a run is in progress waits for the next run,
/// which starts after that caller's own state write, and callers that arrive meanwhile share that single next run.
/// So when <see cref="RunAsync"/> returns, a run that started after the caller's write has finished: nothing a caller
/// wrote is left out of the recompute, and N callers that arrive together cost two runs instead of N.
/// </remarks>
public sealed class SyncRecomputeCoalescer
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Slot> _slots = new(StringComparer.Ordinal);

    // Keys whose last recompute failed or did not finish: the next completion runs one whatever changed.
    private readonly HashSet<string> _owed = new(StringComparer.Ordinal);

    /// <summary>Records that the recompute of <paramref name="key"/> failed, so the derived state may be behind the stored rows.</summary>
    public void MarkOwed(string key)
    {
        lock (_gate)
            _owed.Add(key);
    }

    /// <summary>True when a recompute of <paramref name="key"/> is owed; the mark is taken, and the caller runs the recompute.</summary>
    public bool TakeOwed(string key)
    {
        lock (_gate)
            return _owed.Remove(key);
    }

    // The Workspace profile each key was last seen with. The recompute derives different things under another profile.
    private readonly Dictionary<string, string> _profiles = new(StringComparer.Ordinal);

    /// <summary>
    /// Remembers the Workspace profile <paramref name="key"/> is completed under and returns true when it differs from
    /// the one remembered before: versions, version files and dependency state mean something else under another
    /// profile, so the derived state is recomputed whatever the notification changed. The first profile seen for a
    /// key is not a change.
    /// </summary>
    public bool ObserveProfile(string key, string profile)
    {
        lock (_gate)
        {
            var changed = _profiles.TryGetValue(key, out var previous) && !string.Equals(previous, profile, StringComparison.Ordinal);
            _profiles[key] = profile;
            return changed;
        }
    }

    /// <summary>Removes everything remembered for a key whose context no longer exists. A run in progress is not touched.</summary>
    public void Forget(string key)
    {
        lock (_gate)
        {
            _owed.Remove(key);
            _profiles.Remove(key);
            if (_slots.TryGetValue(key, out var slot) && slot.Current is null && slot.Pending is null)
                _slots.Remove(key);
        }
    }

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
