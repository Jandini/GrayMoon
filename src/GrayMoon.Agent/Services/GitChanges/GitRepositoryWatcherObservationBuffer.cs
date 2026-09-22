namespace GrayMoon.Agent.Services.GitChanges;

/// <summary>
/// Bounded ring buffer of recent working-tree observations. Cap keeps Agent memory bounded even under
/// noisy filesystem bursts; oldest entries are dropped.
/// </summary>
public sealed class GitRepositoryWatcherObservationBuffer(int capacity = 64)
{
    private readonly LinkedList<GitRepositoryObservedChange> _items = new();
    private readonly object _gate = new();

    public int Capacity { get; } = Math.Max(1, capacity);

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _items.Count;
            }
        }
    }

    public void Add(GitRepositoryObservedChange observation)
    {
        lock (_gate)
        {
            _items.AddLast(observation);
            while (_items.Count > Capacity)
            {
                _items.RemoveFirst();
            }
        }
    }

    public IReadOnlyList<GitRepositoryObservedChange> Snapshot()
    {
        lock (_gate)
        {
            return _items.ToArray();
        }
    }
}
