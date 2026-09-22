namespace GrayMoon.Agent.Services.GitChanges;

/// <summary>
/// Watcher coverage window for one repository: when GrayMoon was (or was not) observing working-tree
/// activity. Process-local Agent memory only - ages out with the watcher entry.
/// </summary>
public sealed class GitRepositoryWatcherCoverage
{
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? LastObservedAt { get; private set; }
    public DateTimeOffset? EndedAt { get; private set; }
    public DateTimeOffset? LastDiscontinuityAt { get; private set; }

    public void RecordObservation(DateTimeOffset observedAt)
    {
        LastObservedAt = observedAt;
    }

    public void RecordDiscontinuity(DateTimeOffset at)
    {
        LastDiscontinuityAt = at;
    }

    public void MarkEnded(DateTimeOffset at)
    {
        EndedAt = at;
    }
}
