namespace GrayMoon.Common;

/// <summary>
/// Centralized process execution with safe logging. Use for all command-line invocations so logging stays in one place.
/// </summary>
public interface ICommandLineService
{
    /// <summary>
    /// Runs the executable with the given arguments. Optionally sets working directory and feeds stdin.
    /// Logs once at DEBUG after completion (safe parameters, elapsed ms, exit code). Tokens are redacted.
    /// </summary>
    /// <param name="fileName">Executable name or path (e.g. "git", "dotnet").</param>
    /// <param name="arguments">Command-line arguments. Pass null or empty if none.</param>
    /// <param name="workingDirectory">Working directory; null or empty to use current.</param>
    /// <param name="stdin">Optional stdin content; null to not redirect stdin.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Exit code (or -1 if process failed to start), stdout, and stderr.</returns>
    Task<CommandLineResult> RunAsync(
        string fileName,
        string arguments,
        string? workingDirectory = null,
        string? stdin = null,
        CancellationToken cancellationToken = default);
}
