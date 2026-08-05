namespace GrayMoon.Common;

/// <summary>
/// Default timeout applied to every process <see cref="ICommandLineService"/> launches when the caller
/// does not pass an explicit <c>timeout</c>. Callers that know more about a specific command's expected
/// duration (e.g. <c>GitProcessRunner</c> distinguishing network vs local git operations) should pass
/// their own value instead of relying on this default.
/// </summary>
public sealed class ProcessExecutionOptions
{
    public const string SectionName = "ProcessExecution";

    /// <summary>
    /// Seconds a process may run before it is killed (including its full process tree) and the call
    /// returns a synthetic failed result (ExitCode -1, Stderr describing the timeout) instead of hanging
    /// forever. A process that never exits (credential prompt, stuck file lock, unresponsive network
    /// share) previously blocked its caller's worker/semaphore slot indefinitely.
    /// </summary>
    public int DefaultTimeoutSeconds { get; init; } = 60;
}
