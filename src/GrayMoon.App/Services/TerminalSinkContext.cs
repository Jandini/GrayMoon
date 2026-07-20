namespace GrayMoon.App.Services;

/// <summary>
/// Ambient context that routes agent command stream output to the currently executing background job's terminal.
/// Uses AsyncLocal so each concurrent job task has its own sink without threading parameters through every service.
/// </summary>
public static class TerminalSinkContext
{
    private static readonly AsyncLocal<JobTerminalBuffer?> _current = new();

    public static JobTerminalBuffer? Current => _current.Value;

    /// <summary>Sets the ambient sink for the current async call chain. Dispose to clear.</summary>
    public static IDisposable Use(JobTerminalBuffer? sink)
    {
        var previous = _current.Value;
        _current.Value = sink;
        return new RestoreOnDispose(previous);
    }

    /// <summary>
    /// Clears the ambient sink for the current async call chain, even if a background job's terminal is
    /// active. Use this around agent calls whose output is file/diff content rather than a command log
    /// line - e.g. fetching a diff to restore a remembered file selection - so it never gets appended to
    /// a job's LoadingOverlay terminal. Dispose to restore the previous sink.
    /// </summary>
    public static IDisposable Suppress() => Use(null);

    private sealed class RestoreOnDispose(JobTerminalBuffer? previous) : IDisposable
    {
        public void Dispose() => _current.Value = previous;
    }
}
