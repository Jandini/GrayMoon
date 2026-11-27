namespace GrayMoon.Abstractions.Notifications;

/// <summary>
/// When a <see cref="RepositoryStateSnapshot"/> was read, on the clock of the Worker process that read it: a
/// counter that only goes up, taken once before the first read and once after the last. It is evidence of capture
/// order, which the time the App received a snapshot is not (a snapshot read first can arrive last).
/// <para>
/// Two snapshots of one Worker session compare like intervals: one that ended before the other began is older; two
/// that overlap cannot be ordered, and the App then reads the repository again instead of guessing. Snapshots of
/// different sessions (the Worker restarted) cannot be compared at all.
/// </para>
/// </summary>
public static class RepositoryStateCaptureClock
{
    private static long _sequence;

    /// <summary>Identifies this Worker process. A new process is a new session, and its counter starts again.</summary>
    public static string Session { get; } = Guid.NewGuid().ToString("N");

    /// <summary>The next value of the process-wide counter.</summary>
    public static long Next() => Interlocked.Increment(ref _sequence);
}
