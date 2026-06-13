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
    public static IDisposable Use(JobTerminalBuffer sink)
    {
        _current.Value = sink;
        return new ClearOnDispose();
    }

    private sealed class ClearOnDispose : IDisposable
    {
        public void Dispose() => _current.Value = null;
    }
}
