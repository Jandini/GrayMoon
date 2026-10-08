namespace GrayMoon.Worker.Models;

/// <summary>What happened to one process the user asked GrayMoon to end. Wire values; the App turns them into text.</summary>
public static class TerminateProcessOutcome
{
    /// <summary>Ended and exited.</summary>
    public const string Killed = "Killed";

    /// <summary>Already gone before GrayMoon got to it.</summary>
    public const string AlreadyExited = "AlreadyExited";

    /// <summary>Still running but no longer holding any of the Feature folders, so it was left alone.</summary>
    public const string NotHoldingAnymore = "NotHoldingAnymore";

    /// <summary>The process id now belongs to a different process (or its start time could not be confirmed); left alone.</summary>
    public const string StartTimeChanged = "StartTimeChanged";

    /// <summary>A process GrayMoon never ends (system, service, Explorer, GrayMoon itself).</summary>
    public const string Protected = "Protected";

    /// <summary>The Worker is not allowed to end it (elevated, or another user's process).</summary>
    public const string AccessDenied = "AccessDenied";

    /// <summary>Asked to end, but it did not exit in time or the request failed.</summary>
    public const string Failed = "Failed";
}
