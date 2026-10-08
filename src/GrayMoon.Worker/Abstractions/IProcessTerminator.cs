namespace GrayMoon.Worker.Abstractions;

/// <summary>
/// Ends one local process. Only <see cref="Commands.TerminateBlockingProcessesCommand"/> uses it, and only for a process the
/// user selected and the Worker has just confirmed is holding a Feature folder.
/// </summary>
public interface IProcessTerminator
{
    /// <summary>
    /// Ends <paramref name="processId"/> if it is still the process that started at <paramref name="startTimeUtc"/>, then waits
    /// up to <paramref name="waitForExit"/> for it to exit. Returns a <see cref="Models.TerminateProcessOutcome"/> value.
    /// </summary>
    string Terminate(int processId, DateTime startTimeUtc, TimeSpan waitForExit);

    /// <summary>True when a process with this id is running.</summary>
    bool IsRunning(int processId);
}
