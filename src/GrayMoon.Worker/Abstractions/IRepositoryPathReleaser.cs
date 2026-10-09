namespace GrayMoon.Worker.Abstractions;

/// <summary>
/// Lets an operation that deletes folders (worktree removal) make the Worker let go of every OS handle it holds on
/// those folders first: file system watchers are disposed and no new watcher or <c>git status</c> scan (whose
/// working directory would also pin the folder) is started for them until the returned scope is disposed.
/// </summary>
public interface IRepositoryPathReleaser
{
    /// <summary>
    /// Releases the Worker's watchers on <paramref name="paths"/> and anything beneath them, waits for scans already
    /// running there to finish, and blocks new ones until the returned scope is disposed. Overlapping scopes are
    /// reference counted. Dispose is idempotent and must run on every exit path.
    /// </summary>
    Task<IDisposable> ReleaseAsync(IReadOnlyCollection<string> paths, CancellationToken cancellationToken);
}
