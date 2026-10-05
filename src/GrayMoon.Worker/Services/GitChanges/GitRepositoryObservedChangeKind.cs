namespace GrayMoon.Worker.Services.GitChanges;

/// <summary>Kind of working-tree filesystem event observed by <see cref="GitRepositoryWatcher"/>.</summary>
public enum GitRepositoryObservedChangeKind
{
    Changed,
    Created,
    Deleted,
    Renamed,
}
