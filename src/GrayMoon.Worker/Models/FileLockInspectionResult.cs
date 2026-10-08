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
    /// <summary>The process has a file or folder under the path open.</summary>
    OpenFile = 0,
    /// <summary>The process's current directory is the path or a folder under it (typical for a shell or an AI coding tool).</summary>
    WorkingDirectory = 1,
    /// <summary>The process runs a program or library from under the path (a mapped image or data file, no open handle needed).</summary>
    LoadedModule = 2
}

/// <summary>Why GrayMoon refuses to end a blocking process. Wire values; the App turns them into text.</summary>
public static class BlockingProcessProtectedReason
{
    /// <summary>A system process (Critical kind, or a core Windows process).</summary>
    public const string System = "System";

    /// <summary>A Windows service (runs in session 0).</summary>
    public const string Service = "Service";

    /// <summary>Windows Explorer: ending it restarts the taskbar and desktop, so the user closes the window instead.</summary>
    public const string Explorer = "Explorer";

    /// <summary>A GrayMoon process (the Worker, the App or GrayMoon Desktop).</summary>
    public const string GrayMoon = "GrayMoon";

    /// <summary>The Worker is not allowed to end it (elevated, or another user's process).</summary>
    public const string AccessDenied = "AccessDenied";
}

/// <summary>One process that keeps a path in use. Local diagnostics only: never persisted or sent to a connector.</summary>
/// <param name="ProcessId">Process id.</param>
/// <param name="ProcessName">Friendly application name when Windows knows one, otherwise the executable name.</param>
/// <param name="ExecutablePath">Full executable path when it could be read, otherwise null.</param>
/// <param name="ServiceName">Service short name when <paramref name="Kind"/> is <see cref="BlockingProcessKind.Service"/>.</param>
/// <param name="Kind">What kind of program it is.</param>
/// <param name="Reason">Why it blocks the path.</param>
/// <param name="StartTimeUtc">When the process started; with <paramref name="ProcessId"/> it identifies the process, so a reused id is never mistaken for it. Null when it could not be read.</param>
/// <param name="CanTerminate">True when GrayMoon may end the process after the user confirms (not protected, and the Worker has the right to).</param>
/// <param name="ProtectedReason">A <see cref="BlockingProcessProtectedReason"/> value when <paramref name="CanTerminate"/> is false; otherwise null.</param>
public sealed record BlockingProcessInfo(
    int ProcessId,
    string? ProcessName,
    string? ExecutablePath,
    string? ServiceName,
    BlockingProcessKind Kind,
    BlockingProcessReason Reason,
    DateTime? StartTimeUtc = null,
    bool CanTerminate = false,
    string? ProtectedReason = null);

/// <summary>Result of looking for processes that keep a path in use.</summary>
/// <param name="Processes">Blocking processes, one entry per process id.</param>
/// <param name="MayBeIncomplete">
/// True when the list may miss blockers: the check ran out of time, a process or handle could not be read, an unsupported
/// platform, or a lookup failure. An empty, complete list still does not prove nothing holds the path (for example a virus
/// scanner that already let go).
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
