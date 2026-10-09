namespace GrayMoon.Worker.Abstractions;

/// <summary>Whether a holder of a folder only reads it (may be cancelled at once) or changes it (gets a grace period).</summary>
public enum RepositoryAccessKind
{
    ReadOnly,
    Mutating,
}

/// <summary>Something that keeps an OS handle on a folder across calls (a file system watcher) and can let go on request.</summary>
public interface IReleasable
{
    /// <summary>Drops every OS handle on the folder, permanently. Must be idempotent and must not restart itself.</summary>
    void Release();
}

/// <summary>
/// Notified when a removal starts or ends strictly beneath the folder it is registered for, so a holder of an ancestor
/// folder (a watcher with subdirectories) can ignore the storm of events instead of restarting mid-delete. Called once per
/// removal scope, so an implementation must count overlapping scopes.
/// </summary>
public interface IRepositoryAccessObserver
{
    void ClaimBeneathStarted();

    void ClaimBeneathEnded();
}

/// <summary>A shared use of a folder (a git process, an open repository, a file walk) for as long as it is held.</summary>
public interface IRepositoryAccessLease : IDisposable
{
    /// <summary>Cancelled when a removal needs this folder. Every holder must honour it: stop work and dispose the lease.</summary>
    CancellationToken Yield { get; }
}

/// <summary>An exclusive claim on folders being deleted. Dispose lifts it; idempotent.</summary>
public interface IRepositoryExclusiveScope : IDisposable
{
}

/// <summary>Grace period and ceiling used when a removal has to evict holders.</summary>
public sealed record RepositoryAccessTimings(TimeSpan MutatingGrace, TimeSpan Ceiling, TimeSpan ForceSettle)
{
    public static RepositoryAccessTimings Default { get; } =
        new(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(1));
}

/// <summary>Thrown when a removal could not clear every holder the Worker owns. A defect, not a normal outcome.</summary>
public sealed class RepositoryAccessException(string message) : Exception(message);

/// <summary>
/// The single owner of "who may touch this folder right now". Anything that holds a repository folder takes a shared
/// lease for exactly that lifetime; anything that deletes folders takes an exclusive claim, which refuses new shared
/// leases, releases watchers, cancels readers at once, gives writers a grace period, then cancels them, and finally
/// terminates the processes the Worker itself started. It never touches programs the Worker did not start.
/// </summary>
public interface IRepositoryAccess
{
    /// <summary>
    /// Takes a shared lease on <paramref name="path"/>, or returns null while a removal covers it (the path equals or lies
    /// beneath a claimed folder). <paramref name="forceTerminate"/> kills the process behind the lease, used only as a
    /// last resort. <paramref name="owner"/> lets the remover itself work inside its own claim.
    /// </summary>
    IRepositoryAccessLease? TryAcquireShared(
        string path,
        RepositoryAccessKind kind,
        Action? forceTerminate = null,
        IRepositoryExclusiveScope? owner = null);

    /// <summary>
    /// Claims <paramref name="paths"/> for deletion and returns once the Worker holds no handle on them, or throws
    /// <see cref="RepositoryAccessException"/> (the claim is then withdrawn).
    /// </summary>
    Task<IRepositoryExclusiveScope> AcquireExclusiveAsync(
        IReadOnlyCollection<string> paths,
        CancellationToken cancellationToken,
        RepositoryAccessTimings? timings = null);

    /// <summary>Registers a long-lived handle holder; released when a claim covers <paramref name="path"/>. Dispose to unregister.</summary>
    IDisposable RegisterReleasable(string path, IReleasable releasable);

    /// <summary>Registers an observer for removals strictly beneath <paramref name="path"/>. Dispose to unregister.</summary>
    IDisposable RegisterAncestorObserver(string path, IRepositoryAccessObserver observer);

    bool IsUnderRemoval(string path);
}
