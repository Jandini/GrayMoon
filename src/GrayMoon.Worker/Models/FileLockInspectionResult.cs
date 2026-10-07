namespace GrayMoon.Worker.Models;

/// <summary>What kind of program holds a path, as far as the operating system can tell.</summary>
public enum BlockingProcessKind
{
    Unknown = 0,
    /// <summary>A program with a window, or a background process that is not one of the kinds below.</summary>
    Application = 1,
    /// <summary>A Windows service.</summary>
    Service = 2,
    /// <summary>Windows Explorer (an open folder window, a preview pane or a shell extension).</summary>
    Explorer = 3,
    /// <summary>A console program (a terminal, a shell, a build or test runner).</summary>
    Console = 4,
    /// <summary>A system process that cannot be closed by the user.</summary>
    Critical = 5
}

/// <summary>Why a process blocks the path.</summary>
public enum BlockingProcessReason
{
    /// <summary>The process has a file under the path open (reported by Windows Restart Manager).</summary>
    OpenFile = 0,
    /// <summary>The process's current directory is the path or a folder under it (typical for a shell or an AI coding tool).</summary>
    WorkingDirectory = 1
}

/// <summary>One process that keeps a path in use. Local diagnostics only: never persisted or sent to a connector.</summary>
/// <param name="ProcessId">Process id.</param>
/// <param name="ProcessName">Friendly application name when Windows knows one, otherwise the executable name.</param>
/// <param name="ExecutablePath">Full executable path when it could be read, otherwise null.</param>
/// <param name="ServiceName">Service short name when <paramref name="Kind"/> is <see cref="BlockingProcessKind.Service"/>.</param>
/// <param name="Kind">What kind of program it is.</param>
/// <param name="Reason">Why it blocks the path.</param>
public sealed record BlockingProcessInfo(
    int ProcessId,
    string? ProcessName,
    string? ExecutablePath,
    string? ServiceName,
    BlockingProcessKind Kind,
    BlockingProcessReason Reason);

/// <summary>Result of looking for processes that keep a path in use.</summary>
/// <param name="Processes">Blocking processes, one entry per process id.</param>
/// <param name="MayBeIncomplete">
/// True when the list may miss blockers: too many files to check, a process that could not be read, an unsupported platform,
/// or a Restart Manager failure. An empty, complete list still does not prove nothing holds the path (for example a virus scanner
/// that already let go).
/// </param>
/// <param name="Diagnostic">Short, user-safe explanation of why the list may be incomplete; null when there is nothing to add.</param>
public sealed record FileLockInspectionResult(
    IReadOnlyList<BlockingProcessInfo> Processes,
    bool MayBeIncomplete,
    string? Diagnostic)
{
    public static FileLockInspectionResult Empty { get; } = new([], false, null);

    public static FileLockInspectionResult Unsupported(string diagnostic) => new([], true, diagnostic);
}
